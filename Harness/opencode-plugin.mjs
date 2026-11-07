/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

import fs from "node:fs";
import os from "node:os";
import path from "node:path";

// Conduit's in-harness half for OpenCode, and the reason it is a PLUGIN rather than anything else is
// the same reason the vendor's own control plane is unreachable otherwise: a plugin is handed `client`,
// an already-authenticated handle to OpenCode's HTTP control plane, and every hook it registers is told
// which session it is running for. An MCP server is handed neither - OpenCode calls one with the tool's
// name and arguments and nothing else - and the control plane's password is generated per launch and
// delivered to the sidecar over IPC rather than through the environment, so nothing outside the harness
// can authenticate to it or even learn its port.
//
// It exists because a Unity world build outlives the call that started it. The editor answers a verb
// that finishes inside the client's own time limit on the call itself; a build, an upload or a bake
// does not, and by the time it finishes the call has already returned. This side holds a stream so the
// result has somewhere to go, and injects it as a synthetic prompt when it arrives.
//
// A session is attached on its FIRST call to a Conduit tool, with no step the agent has to remember, the
// same way a Claude session is registered by its start-up hook. OpenCode runs a plugin's
// tool.execute.before hook before it sends an MCP call anywhere, so the stream is open by the time the
// call reaches the editor, and a build verb - which checks for its return stream before starting work -
// never sees the session unattached. The session's id is also placed in the environment of every shell
// the agent runs, as CONDUIT_SESSION_ID, which is where the agent reads it to pass to a verb taking one.
//
// The connection runs OUTWARD, from here to the editor, which is what makes this harness work at all.
// The editor cannot discover an address for an OpenCode session - nothing publishes one - so the side
// that is already inside the harness connects to the side that can be found.
//
// Nothing here polls. The stream is held open and the editor writes when it has something; a closed
// socket is itself the disconnect notice, and the session's next Conduit call attaches it again.

const CONFIG_PATH = path.join(os.homedir(), ".config", "opencode", "opencode.json");

// The header every Conduit bridge requires, and the marker that tells a Conduit entry apart from any
// other remote MCP server in the same config. Matched case-insensitively because the config is written
// by hand as often as by the editor.
const IDENTITY_HEADER = "x-conduit-project";

// OpenCode's own rule for turning a server or tool name into part of a tool key: anything outside
// letters, digits, underscore and hyphen becomes an underscore, and the two halves are joined by an
// underscore. Reproduced exactly, because a Conduit call is recognised by the key OpenCode builds for it.
const sanitize = (value) => value.replace(/[^a-zA-Z0-9_-]/g, "_");

const CONNECT_TIMEOUT_MS = 4000;

/**
 * Conduit's OpenCode plugin. Exported as a named Plugin function, which is the form the vendor's own
 * shipped example uses.
 *
 * @param {import("@opencode-ai/plugin").PluginInput} input Plugin context supplied by OpenCode.
 * @returns {Promise<import("@opencode-ai/plugin").Hooks>} Hooks OpenCode will drive.
 */
export const ConduitPlugin = async (input) => {
  const link = createLink(input);

  return {
    // Awaited by OpenCode before the call is sent, which is what makes the attach race-free.
    "tool.execute.before": async (hook) => { await link.ensureAttached(hook.tool, hook.sessionID); },

    "shell.env": async (hook, output) => {
      // A shell outside any session - a terminal pane - has no session to name, and gets nothing.
      if (typeof hook.sessionID === "string" && hook.sessionID.length > 0) {
        output.env.CONDUIT_SESSION_ID = hook.sessionID;
      }
    },

    event: async ({ event }) => {
      if (event?.type === "session.deleted") link.release(event.properties?.info?.id);
    },

    dispose: async () => { link.close(); },
  };
};

function createLink(input) {
  const { client } = input;

  // One entry per session that has called a Conduit tool, mapping each bridge it is attached to onto the
  // cancellation for that bridge's stream. Keyed by session so several OpenCode sessions can each be
  // reported to independently, which is the whole point: a single registration per harness would answer
  // the wrong conversation the moment a second one opened.
  const sessions = new Map();

  // Attachments still connecting, keyed by session and bridge, so two Conduit calls a session makes in
  // parallel wait on the one connect rather than opening two streams to the same bridge.
  const pending = new Map();
  let closed = false;

  /**
   * Reads every Conduit bridge out of OpenCode's own global config.
   *
   * ONE installed copy of this plugin serves all of them, which is the whole reason it discovers them
   * instead of being pinned to one at install. OpenCode imports every registered plugin at startup
   * whether or not the project behind it is open, so a copy per project would cost a module per project
   * ever connected - and every copy would register the same tool name, leaving whichever loaded last to
   * answer for all of them.
   *
   * The config is also the source of truth for the ENDPOINT rather than anything stored here, because
   * the editor picks a fresh port each run and rewrites its own entry accordingly.
   *
   * @returns {Array<{name: string, url: string, headers: Record<string, string>}>} Bridges found.
   */
  function bridges() {
    let parsed;
    try { parsed = JSON.parse(fs.readFileSync(CONFIG_PATH, "utf8")); }
    catch { return []; }

    const servers = parsed?.mcp;
    if (servers === null || typeof servers !== "object") return [];

    const found = [];
    for (const [name, entry] of Object.entries(servers)) {
      if (entry === null || typeof entry !== "object") continue;
      if (entry.enabled === false) continue;
      if (typeof entry.url !== "string" || entry.url.length === 0) continue;

      const headers = entry.headers;
      if (headers === null || typeof headers !== "object") continue;

      // The identity header is what tells a Conduit entry apart from any other remote MCP server in the
      // same config; its value is the project, which is why several entries legitimately carry it.
      const carriesIdentity = Object.keys(headers)
        .some((key) => key.toLowerCase() === IDENTITY_HEADER);
      if (!carriesIdentity) continue;

      found.push({ name, url: entry.url, headers });
    }

    return found;
  }

  /**
   * Turns a bridge's MCP endpoint into its agent-stream endpoint, preserving everything else about it.
   *
   * @param {string} endpoint The `url` from the config entry.
   * @param {string} sessionId Session the stream will deliver to.
   * @returns {string} Absolute stream URL.
   */
  function streamUrl(endpoint, sessionId) {
    const parsed = new URL(endpoint);
    parsed.pathname = "/agent-stream";
    parsed.search = `?session=${encodeURIComponent(sessionId)}`;
    return parsed.toString();
  }

  /**
   * Injects one notice into a session as a synthetic prompt.
   *
   * Synthetic is load-bearing. The notice arrives in the conversation in the same position a typed
   * message would, so marking it is what keeps a build report from reading as the owner asking for
   * something. The wrapper the editor puts around the text says the same thing in words.
   *
   * @param {string} sessionId Session to inject into.
   * @param {string} text Notice body as the editor framed it.
   */
  async function inject(sessionId, text) {
    // throwOnError is mandatory: the generated client returns HTTP failures as DATA, so without it a
    // refused injection resolves successfully and this would report a delivery that never happened.
    await client.session.promptAsync({
      path: { id: sessionId },
      body: { parts: [{ type: "text", text, synthetic: true }] },
      throwOnError: true,
    });
  }

  /**
   * Opens one bridge's stream for one session, and returns once that bridge has accepted it.
   *
   * Each bridge gets its OWN cancellation, which is not a detail: several Unity projects run their own
   * Conduit at the same time, so one of them being closed must not disturb the streams belonging to
   * the ones that are open. A single shared cancellation here means the first unreachable project
   * tears down every live project's delivery.
   *
   * @param {{name: string, url: string, headers: Record<string, string>}} bridge Bridge to attach to.
   * @param {string} sessionId Session this stream reports to.
   * @returns {Promise<{abort: AbortController, finished: Promise<void>}>} The held stream.
   */
  async function attach(bridge, sessionId) {
    const abort = new AbortController();

    // Bounds the CONNECT only, and is cleared the moment the bridge answers. Left armed it would
    // cancel a healthy stream a few seconds into its life.
    const timeout = setTimeout(() => abort.abort(), CONNECT_TIMEOUT_MS);

    let response;
    try {
      response = await fetch(streamUrl(bridge.url, sessionId), {
        headers: { ...bridge.headers, Accept: "text/event-stream" },
        signal: abort.signal,
      });
    } catch (error) {
      abort.abort();
      throw error;
    } finally {
      clearTimeout(timeout);
    }

    if (!response.ok || response.body === null) {
      abort.abort();
      throw new Error(`the bridge answered ${response.status} rather than opening a stream`);
    }

    // Awaiting the fetch IS the attach confirmation: it resolves when the response head arrives, and
    // for a held stream that only happens once the editor has taken the socket and written it. A
    // caller that reports success without awaiting this reports every closed project as attached.
    return { abort, finished: pump(response, bridge, sessionId, abort) };
  }

  /**
   * Reads one open stream to its end, injecting each notice as it arrives.
   *
   * @param {Response} response The accepted stream response.
   * @param {{name: string}} bridge Bridge the stream belongs to.
   * @param {string} sessionId Session this stream reports to.
   * @param {AbortController} abort This stream's own cancellation.
   * @returns {Promise<void>} Resolves when the stream ends.
   */
  async function pump(response, bridge, sessionId, abort) {
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    for (;;) {
      const { done, value } = await reader.read();

      // The signal is checked as well as awaited on. A cancelled read usually rejects, but a frame
      // already buffered can resolve first, and injecting from a stream this session has torn down
      // would deliver a notice for an editor it is no longer attached to.
      if (done || closed || abort.signal.aborted) return;

      buffer += decoder.decode(value, { stream: true });

      // Frames are separated by a blank line. Anything before the last separator is complete; the
      // remainder stays buffered, because a frame can and does arrive split across reads.
      let separator = buffer.indexOf("\n\n");
      while (separator >= 0) {
        const frame = buffer.slice(0, separator);
        buffer = buffer.slice(separator + 2);
        separator = buffer.indexOf("\n\n");

        await consume(frame, sessionId, bridge);
      }
    }
  }

  /**
   * Acts on one complete stream frame.
   *
   * @param {string} frame Raw frame text, without its trailing blank line.
   * @param {string} sessionId Session this stream belongs to.
   * @param {{name: string}} bridge Bridge the frame came from.
   */
  async function consume(frame, sessionId, bridge) {
    let payload;
    for (const line of frame.split("\n")) {
      // Comment lines carry the keepalive that holds the socket open, and the open marker. Neither is
      // data, and a parser that treated them as such would fail on every keepalive.
      if (line.startsWith(":") || !line.startsWith("data:")) continue;
      try { payload = JSON.parse(line.slice(5).trim()); }
      catch { return; }
    }

    if (payload?.type !== "notice") return;
    if (typeof payload.text !== "string" || payload.text.length === 0) return;

    // Checked rather than trusted. A plugin holds one stream per bridge per session, so a frame naming
    // a different session means the streams have been crossed somewhere, and injecting it would put
    // one conversation's build report into another's.
    if (typeof payload.sessionId === "string" && payload.sessionId !== sessionId) return;

    try { await inject(sessionId, payload.text); }
    catch (error) {
      // Reported to the harness log rather than swallowed, and rather than thrown: one failed
      // injection must not tear down a stream that is still carrying notices for this session.
      console.error(
        `[Conduit] could not inject a notice from ${bridge.name} into session ${sessionId}:`,
        error instanceof Error ? error.message : String(error));
    }
  }

  /**
   * Finds the Conduit bridge an OpenCode tool belongs to, or none.
   *
   * OpenCode keys an MCP tool as its sanitised server name, an underscore, and its sanitised tool name,
   * so a Conduit call is one whose key begins with a Conduit bridge's name and that underscore. That tells
   * it apart from every other tool - a file read, a shell, another MCP server - without keeping a list of
   * Conduit's verb names that would go stale. The LONGEST matching name wins, so a bridge whose name is a
   * prefix of another's cannot claim the other's tools.
   *
   * @param {string} toolKey Tool key OpenCode passes to its hooks.
   * @returns {{name: string, url: string, headers: Record<string, string>} | undefined} Its bridge.
   */
  function bridgeForTool(toolKey) {
    if (typeof toolKey !== "string") return undefined;

    let match;
    let matchLength = -1;
    for (const bridge of bridges()) {
      const prefix = `${sanitize(bridge.name)}_`;
      if (toolKey.startsWith(prefix) && prefix.length > matchLength) {
        match = bridge;
        matchLength = prefix.length;
      }
    }

    return match;
  }

  /**
   * Makes sure a session holds a stream to the bridge one of its tool calls is about to reach.
   *
   * Runs for every tool call. A tool that is not Conduit's returns once the bridge list - one small
   * config file - has been read, and an attached session returns at the map lookup, so only the first
   * Conduit call per session and bridge does real work. It attaches only the bridge being called, because
   * that is the only editor this session's id can be handed to and so the only one that can report to
   * it. It never throws - a throw here would abort the agent's tool call - so a bridge that refuses is
   * left for the verb itself to report, which a build verb does before it starts work.
   *
   * @param {string} toolKey Tool the session is calling.
   * @param {string} sessionId Session making the call.
   * @returns {Promise<void>} Resolves once the stream is held or the attempt has failed.
   */
  async function ensureAttached(toolKey, sessionId) {
    if (closed || typeof sessionId !== "string" || sessionId.length === 0) return;

    const bridge = bridgeForTool(toolKey);
    if (bridge === undefined) return;
    if (sessions.get(sessionId)?.has(bridge.name)) return;

    const key = `${sessionId}\n${bridge.name}`;
    const inFlight = pending.get(key);
    if (inFlight !== undefined) { await inFlight; return; }

    const attempt = (async () => {
      try {
        const held = await attach(bridge, sessionId);

        // A plugin closed while this was connecting keeps nothing it opened.
        if (closed) { held.abort.abort(); return; }

        let streams = sessions.get(sessionId);
        if (streams === undefined) {
          streams = new Map();
          sessions.set(sessionId, streams);
        }
        streams.set(bridge.name, held.abort);

        // Not awaited: the stream runs until the editor closes it. Its rejection is handled here so an
        // editor closing cannot surface as an unhandled rejection, and it names which project. When it
        // ends, only THIS stream is forgotten, and only while it is still the one on record, so the
        // session's next call to that bridge attaches again and its other bridges are left alone.
        void held.finished
          .catch((error) => {
            if (held.abort.signal.aborted || closed) return;
            console.error(
              `[Conduit] the stream to ${bridge.name} for session ${sessionId} ended:`,
              error instanceof Error ? error.message : String(error));
          })
          .finally(() => {
            const current = sessions.get(sessionId);
            if (current === undefined || current.get(bridge.name) !== held.abort) return;
            current.delete(bridge.name);
            if (current.size === 0) sessions.delete(sessionId);
          });
      } catch (error) {
        console.error(
          `[Conduit] could not attach session ${sessionId} to ${bridge.name}:`,
          error instanceof Error ? error.message : String(error));
      } finally {
        pending.delete(key);
      }
    })();

    pending.set(key, attempt);
    await attempt;
  }

  /**
   * Drops every stream a session holds, once OpenCode has deleted that session.
   *
   * @param {string | undefined} sessionId Session to release.
   */
  function release(sessionId) {
    if (typeof sessionId !== "string") return;

    const streams = sessions.get(sessionId);
    if (streams === undefined) return;

    for (const abort of streams.values()) abort.abort();
    sessions.delete(sessionId);
  }

  function close() {
    closed = true;
    for (const streams of sessions.values()) {
      for (const abort of streams.values()) abort.abort();
    }

    sessions.clear();
  }

  return { ensureAttached, release, close };
}
