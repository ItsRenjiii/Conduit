/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Text;
using System.Threading;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Unity Package Manager commands that synchronously wait for short editor package requests.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Package Commands

        /// <summary>
        /// Adds a package by registry name, Git URL, tarball, or local package path.
        /// </summary>
        /// <param name="args">The package identifier or URL.</param>
        /// <returns>A compact JSON package result or a Package Manager error.</returns>
        private static string PackageAddCommand(string args)
        {
            var packageNameOrUrl = (args ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(packageNameOrUrl))
            {
                return "Usage: package-add <packageNameOrUrl>";
            }

            var request = Client.Add(packageNameOrUrl);
            var error = WaitForPackageRequest(request, "add package");
            if (error != null)
            {
                return error;
            }

            // Resolving a package rewrites the assembly set, so a compile and domain reload follow the
            // request completing. Withhold the response until they settle, or the caller's next command
            // queues behind work this one started and times out in a way that reads like a bridge fault.
            var added = JsonPackageInfo(request.Result);
            if (TryDeferUntilReload("__deferred-note " + added))
            {
                return string.Empty; // withheld; CompletePendingDeferred writes the real result post-reload
            }

            return added;
        }

        /// <summary>
        /// Lists installed Unity packages.
        /// </summary>
        /// <param name="args">Optional offline token; any value equal to offline enables offline mode.</param>
        /// <returns>A compact JSON array of package info.</returns>
        private static string PackageListCommand(string args)
        {
            var offline = string.Equals((args ?? string.Empty).Trim(), "offline", StringComparison.OrdinalIgnoreCase);
            var request = Client.List(offline, true);
            var error = WaitForPackageRequest(request, "list packages");
            if (error != null)
            {
                return error;
            }

            var builder = new StringBuilder();
            builder.Append('[');
            var index = 0;
            foreach (var packageInfo in request.Result)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonPackageInfo(packageInfo));
                index++;
            }

            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>
        /// Removes an installed package by package name.
        /// </summary>
        /// <param name="args">The package name to remove.</param>
        /// <returns>A confirmation line or Package Manager error.</returns>
        private static string PackageRemoveCommand(string args)
        {
            var packageName = (args ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(packageName))
            {
                return "Usage: package-remove <packageName>";
            }

            var request = Client.Remove(packageName);
            var error = WaitForPackageRequest(request, "remove package");
            if (error != null)
            {
                return error;
            }

            // Removing a package rewrites the assembly set, so a compile and domain reload follow it, the
            // same as package-add. Withhold the response until they settle.
            var removed = "Removed package " + packageName + ".";
            if (TryDeferUntilReload("__deferred-note " + removed))
            {
                return string.Empty; // withheld; CompletePendingDeferred writes the real result post-reload
            }

            return removed;
        }

        /// <summary>
        /// Searches the Unity package registry and filters results locally for the requested query.
        /// </summary>
        /// <param name="args">The package search text.</param>
        /// <returns>A compact JSON array of matching package info.</returns>
        private static string PackageSearchCommand(string args)
        {
            var query = (args ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(query))
            {
                return "Usage: package-search <query>";
            }

            var request = Client.SearchAll(false);
            var error = WaitForPackageRequest(request, "search packages");
            if (error != null)
            {
                return error;
            }

            var builder = new StringBuilder();
            var count = 0;
            builder.Append('[');
            foreach (var packageInfo in request.Result)
            {
                if (packageInfo.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    packageInfo.displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (count > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonPackageInfo(packageInfo));
                count++;
                if (count >= DefaultJsonResultLimit)
                {
                    break;
                }
            }

            builder.Append(']');
            return builder.ToString();
        }

        #endregion

        #region Package Helpers

        /// <summary>
        /// Waits briefly for a Package Manager request so the text bridge can return a useful result.
        /// </summary>
        /// <param name="request">The request to wait for.</param>
        /// <param name="label">The human-readable request label used in errors.</param>
        /// <returns>An error string when the request fails or times out, otherwise null.</returns>
        private static string WaitForPackageRequest(Request request, string label)
        {
            // Cap the wait so the text bridge always returns rather than blocking indefinitely on a stalled request.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!request.IsCompleted && DateTime.UtcNow < deadline)
            {
                // Poll interval (ms): short enough to feel responsive, long enough to avoid busy-spinning the editor thread.
                Thread.Sleep(25);
            }

            if (!request.IsCompleted)
            {
                return "Timed out waiting to " + label + ".";
            }

            if (request.Status == StatusCode.Failure)
            {
                return request.Error == null ? "Package Manager failed to " + label + "." : request.Error.message;
            }

            return null;
        }

        /// <summary>
        /// Encodes Unity package metadata used by list/search/add responses.
        /// </summary>
        /// <param name="packageInfo">The package metadata returned by Package Manager.</param>
        /// <returns>A compact JSON package object.</returns>
        private static string JsonPackageInfo(UnityEditor.PackageManager.PackageInfo packageInfo)
        {
            if (packageInfo == null)
            {
                return "null";
            }

            return "{\"name\":" + JsonQuote(packageInfo.name) +
                ",\"displayName\":" + JsonQuote(packageInfo.displayName) +
                ",\"version\":" + JsonQuote(packageInfo.version) +
                ",\"source\":" + JsonQuote(packageInfo.source.ToString()) +
                ",\"resolvedPath\":" + JsonQuote(packageInfo.resolvedPath) + "}";
        }

        #endregion
            /// <summary>
        /// Routes the Package verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchPackage(string verb, string command, string[] parts)
        {
            if (verb == "package-add")
            {
                return PackageAddCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "package-list")
            {
                return PackageListCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "package-remove")
            {
                return PackageRemoveCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "package-search")
            {
                return PackageSearchCommand(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }
    }
}
