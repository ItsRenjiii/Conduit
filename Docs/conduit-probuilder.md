# ProBuilder

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before modelling world geometry in the editor.**
>
> **This file owns using ProBuilder through Conduit, and nothing else goes in it.** What a mesh costs to
> render is `conduit-rendering-cost.md`, sizing a collider against geometry is `conduit-scene-geometry.md`,
> and the bake is `conduit-lighting.md`.

## Using it

- **ProBuilder is the in-editor modelling path and it works in a VRChat world.** Geometry authored with
  it ships as an ordinary mesh: no export step, no conversion, no special handling at build time. It is
  how a creator who does not model in Blender builds world geometry, and it is not something to warn a
  user about or ask to validate.
- **Install it with `package-add com.unity.probuilder`**, which blocks until the import and compile have
  finished, so nothing after it needs polling. `package-search probuilder` confirms the registry entry
  first. (ProBuilder 5.2.4)
- **READ ProBuilder's own page for an action before driving its verb.** The package ships its whole manual
  as markdown in a `Documentation~` folder - `TableOfContents.md` indexes it, and the `workflow-*` pages
  are the craft rather than the button list. Locate the folder inside the installed package rather than
  writing a path down, because the path carries the version. Deriving behaviour from a verb's reply
  instead has already produced rules that were only true in one configuration.
- **Each verb is named after the action's own page, so the manual and the verb list line up.** An action
  that exists per element keeps the element first, as the pages do - `Face_Extrude` is
  `probuilder-face-extrude`, `Vert_Weld` is `probuilder-vert-weld`. One that spans elements takes the
  action's name alone, because no element prefix would be true: `probuilder-triangulate` covers both
  `Face_Triangulate` and `Object_Triangulate`. Anything with no page is Conduit's own and says so.
- **Every ProBuilder operation is a `probuilder-*` verb** - shapes, selection, face and vertex editing,
  UVs, materials, smoothing, normals, topology, pivots, and the asset steps. The catalogue publishes what
  each one takes; there is no reason to drive the ProBuilder window through `menu` or `editor-window-click`.
- **FACE INDEXES SHIFT the moment a verb adds or removes faces**, and every such verb says so in its
  result. Re-read them with `probuilder-mesh-get` between operations rather than reusing indexes from
  before the previous call - stale indexes address different geometry, silently.
- **A shape needs BOTH lightmap UVs and the static flags before it bakes, and the STATIC half fails
  silently.** `probuilder-shape-create` sets the static flags, and `probuilder-lightmap-static` sets them
  on geometry that arrived any other way - anything `probuilder-ize` converted has none.
  `probuilder-lightmap-uvs` generates the UV2, and `probuilder-validate` reports both without changing
  anything. Geometry missing either renders perfectly and lights as though it were not there. ProBuilder
  can warn about the UV2 half by itself at bake time, through its Show Missing Lightmap UVs Warning
  preference; nothing warns about the static flags.
  (ProBuilder `Documentation~/preferences.md`)
- **Size geometry with `probuilder-fit-to-size`, not by scaling the transform**, which distorts children
  and colliders.
- **A collider does NOT follow edited geometry.** A shape that was extruded or subdivided keeps its old
  collision until `probuilder-colliders-rebuild` runs, which is why a player walks through a wall that
  plainly looks solid.
- **Projecting UVs makes a face MANUAL, and auto settings stop reaching it.** `probuilder-uv-set` writes
  the auto-UV panel; `probuilder-uv-project` overwrites the UVs outright with a box, sphere or planar
  layout. Project first, adjust after.
- **Every UV0 EDIT needs the face on manual UVs, or the next rebuild regenerates it and the edit is gone.**
  `probuilder-uv-split`, `-collapse`, `-sew`, `-stitch`, `-flip`, `-fit` and `-transform` put the faces
  they touch on manual UVs for exactly this reason and say how many; `probuilder-uv-mode` sets the flag
  on its own. An auto-UV face recomputes its UVs from the auto panel on every refresh, so a write that is
  not protected this way vanishes without any error - which is why the editing verbs read the coordinates
  back and tell you how many HELD rather than assuming.
- **UV2 is the LIGHTMAP channel and behaves the opposite way round, so never reason from UV0 to it.**
  The verbs that take `channel:uv0|uv2` are project, stitch, flip, fit and transform; split, collapse and
  sew are UV0-only because ProBuilder itself refuses them on UV2. The manual-UV flag governs UV0 alone and
  does nothing for UV2, a mesh rebuild DISCARDS a UV2 edit rather than preserving it, and
  `probuilder-lightmap-uvs` regenerates the whole channel - so hand-tuned UV2 is written after that
  generation, never before it.
- **Build a non-box plan with `probuilder-poly-create`, not by cutting a cube about.** It takes the
  footprint corners and an extrude depth, and its `holes` option cuts doorways and windows straight
  through in the same call - so an L-shaped room is one call rather than a subtract-and-repair sequence.
  The corners go AFTER the pipe (`<name> | x y z, x y z, ...`); passed in the token slot they are quoted
  on the way in and the quote marks arrive as part of the first corner.
- **Divide a face with `probuilder-cut`**, which drives ProBuilder's Cut tool with the path points rather
  than clicking them: edge-to-edge to split a face in two, a path repeating its first point to cut a shape
  out of one. The reply names how ProBuilder classified each point, and that classification is the whole
  story - a point it reads as `AddedOnEdge` cuts to that edge, one it reads as `NewVertex` gets connected
  out to the face's own vertices. `probuilder-insert-vertex-on-edge` and `-in-face` remain for adding
  single points without cutting.
- **Anything CURVED and tubular - a pipe, cable, handrail, curved tunnel - is `probuilder-bezier-create`
  rather than a chain of primitives.** It sweeps a tube along a spline through the points given, in the
  same after-the-pipe grammar as `probuilder-poly-create` and for the same reason.
- **Convert geometry that is already in the scene with `probuilder-ize`** rather than rebuilding it: an
  imported model or a Unity primitive becomes editable in place. It refuses anything already ProBuilder,
  and lightmap UVs do NOT survive the conversion.
- **Colour-code a layout with `probuilder-vertex-color-set` instead of a material per zone**, and read it
  back with `probuilder-mesh-get`'s colors section or find it again with `probuilder-select-vertex-color`.
  **But only a material whose shader READS vertex colour displays it**, and ProBuilder's own default
  material does while Unity's Standard does not - so the same call is invisible on one mesh and correct on
  the next, which reads as a failed write rather than a material choice. Check what the object draws with
  before concluding anything. (ProBuilder `Documentation~/workflow-vertexcolors.md`)

## Driving the face verbs

- **Select before every face operation - the face verbs act on the CURRENT selection by default**, and an
  empty one is refused rather than quietly taken as everything. `probuilder-select` sets it, and the face
  slot on each verb also accepts `all` or an explicit index list when a standing selection is not wanted.
- **An EDGE is two vertex indexes joined by a hyphen, and a list of them separates pairs with commas -
  `0-1, 2-3`.** A comma alone could not be told apart from the separator between pairs. Where a verb acts
  on one element kind but the geometry is easier to name as another, the selector takes a prefix:
  `faces:0,1` means every edge of those faces and `edges:0-1` means the vertices behind those edges. That
  is how a whole block gets bevelled, or a face offset, without listing its elements by hand.
- **Whether UV2 generation needs ordering at all depends on ProBuilder's Auto Lightmap UVs preference,
  so read it before relying on either behaviour.** With it ON, ProBuilder regenerates the UV2 after every
  geometry edit and `probuilder-lightmap-uvs` is only needed to force one; with it OFF, a shape edited after
  being optimized silently loses its UV2, so optimize LAST once the geometry is final. The preference is
  project-wide and costs time on dense meshes, which is why it gets turned off.
  (ProBuilder `Documentation~/preferences.md`, `lightmap-uv.md`)
- **Read the size `probuilder-fit-to-size` MEASURED, not the one you asked for.** A zero X or Z is clamped
  so a collider can still be built, so the applied size and the requested size genuinely differ.
- **An OBJ export names its material library after the MESH, not after the file you asked for**, because
  that is the name the exporter wrote into the obj's own `mtllib` line. So `exported-cube.obj` arrives
  beside `Cube.mtl`, and renaming either one by hand breaks the reference that makes the materials load.
- **A shape built with `holes` reports its two capped faces as non-contiguous in `probuilder-validate`,
  and that is correct output rather than a fault.** A cap with a hole through it is not one contiguous
  triangle fan. Build the same footprint without holes and compare before treating the reading as damage.
