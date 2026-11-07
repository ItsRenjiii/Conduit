/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// ProBuilder is an OPTIONAL package, so every line of this file is gated on it being installed. The
// define comes from the versionDefines entry in ItsRenji.Conduit.Editor.asmdef, and without the guard a
// project that never added com.unity.probuilder fails to compile the whole Conduit editor assembly -
// not merely these verbs. Nothing else in Conduit references ProBuilder types directly; the four seams
// that mention these verbs by NAME - the dispatcher, the two catalogue blocks and one schema repair -
// carry the same guard.
#if CONDUIT_PROBUILDER

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Modelling geometry with ProBuilder through the bridge, so a world's shapes can be built and edited
    /// without a modelling package.
    /// </summary>
    /// <remarks>
    /// ProBuilder keeps an authoring mesh beside the rendered one, and the two only agree after a rebuild.
    /// Every verb here that mutates geometry therefore ends at <see cref="RebuildProBuilderMesh"/>, which
    /// runs ToMesh then Refresh - measured as the sequence that moves the MeshFilter's shared mesh, where
    /// mutating alone leaves the rendered mesh at its previous shape while every count on the authoring
    /// object already reads as changed.
    /// <para>
    /// Faces are addressed by INDEX into the mesh's own face list, because an index is the only handle that
    /// survives a round trip through a bridge call. Those indexes shift whenever a verb adds or removes
    /// faces, so a caller that is about to chain operations reads them back with probuilder-mesh-get rather
    /// than reusing indexes from before the previous call.
    /// </para>
    /// </remarks>
    internal static partial class ConduitBridge
    {
        #region Constants

        /// <summary>Selector accepted wherever a verb takes a face list, meaning every face on the mesh.</summary>
        private const string PB_ALL_FACES = "all";

        /// <summary>Selector meaning whatever the mesh currently has selected.</summary>
        private const string PB_SELECTED_FACES = "selected";

        /// <summary>
        /// Instance members of any accessibility, for reaching the fields and methods of ProBuilder
        /// components whose own types are internal to its runtime assembly.
        /// </summary>
        private const System.Reflection.BindingFlags INSTANCE_MEMBERS =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;

        #endregion

        #region Resolution helpers

        /// <summary>
        /// Resolves a scene target to its ProBuilder mesh, distinguishing "no such object" from "that object
        /// is not ProBuilder geometry", because the two need different fixes from the caller.
        /// </summary>
        /// <param name="target">Instance id, scene path or unambiguous name.</param>
        /// <param name="mesh">Receives the resolved mesh.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string TryFindProBuilderMesh(string target, out ProBuilderMesh mesh)
        {
            mesh = null;

            GameObject gameObject;
            var error = TryFindGameObject(target, out gameObject);
            if (error != null) return error;

            mesh = gameObject.GetComponent<ProBuilderMesh>();
            if (mesh == null)
            {
                return "'" + GetGameObjectPath(gameObject) + "' has no ProBuilderMesh, so it is an ordinary "
                    + "mesh rather than editable ProBuilder geometry. probuilder-shape-create makes one.";
            }

            return null;
        }

        /// <summary>
        /// Turns a face selector into concrete faces, validating every index BEFORE any of them is used so a
        /// bad index cannot half-apply an operation.
        /// </summary>
        /// <param name="mesh">Mesh the indexes address.</param>
        /// <param name="selector">"all", "selected", or a comma or space separated index list.</param>
        /// <param name="faces">Receives the resolved faces.</param>
        /// <param name="indexes">Receives the resolved indexes, for reporting what was acted on.</param>
        /// <returns>Null on success, or a user-facing error naming the offending index.</returns>
        private static string TryResolveFaces(
            ProBuilderMesh mesh, string selector, out List<Face> faces, out List<int> indexes)
        {
            faces = new List<Face>();
            indexes = new List<int>();

            var wanted = (selector ?? string.Empty).Trim();
            if (wanted.Length == 0) wanted = PB_SELECTED_FACES;

            if (string.Equals(wanted, PB_ALL_FACES, StringComparison.OrdinalIgnoreCase))
            {
                for (var index = 0; index < mesh.faceCount; index++)
                {
                    faces.Add(mesh.faces[index]);
                    indexes.Add(index);
                }

                return faces.Count > 0 ? null : "This mesh has no faces.";
            }

            if (string.Equals(wanted, PB_SELECTED_FACES, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var index in mesh.selectedFaceIndexes)
                {
                    faces.Add(mesh.faces[index]);
                    indexes.Add(index);
                }

                return faces.Count > 0
                    ? null
                    : "Nothing is selected on this mesh. Select faces with probuilder-select, or pass "
                        + PB_ALL_FACES + " or an explicit index list.";
            }

            var seen = new HashSet<int>();
            foreach (var token in wanted.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int index;
                if (!int.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                {
                    return "'" + token.Trim() + "' is not a face index. Pass " + PB_ALL_FACES + ", "
                        + PB_SELECTED_FACES + ", or numbers separated by commas or spaces.";
                }

                if (index < 0 || index >= mesh.faceCount)
                {
                    return "Face index " + index.ToString(CultureInfo.InvariantCulture)
                        + " is outside this mesh, which has " + mesh.faceCount.ToString(CultureInfo.InvariantCulture)
                        + " faces numbered 0 to "
                        + (mesh.faceCount - 1).ToString(CultureInfo.InvariantCulture) + ".";
                }

                if (!seen.Add(index)) continue;

                faces.Add(mesh.faces[index]);
                indexes.Add(index);
            }

            return faces.Count > 0 ? null : "No faces were named.";
        }

        /// <summary>
        /// Rebuilds the rendered mesh from the authoring data and marks the scene, which every geometry
        /// mutation must end at or the MeshFilter keeps rendering the previous shape.
        /// </summary>
        /// <param name="mesh">Mesh to rebuild.</param>
        private static void RebuildProBuilderMesh(ProBuilderMesh mesh)
        {
            mesh.ToMesh();
            mesh.Refresh(RefreshMask.All);
            EditorUtility.SetDirty(mesh);
            MarkGameObjectSceneDirty(mesh.gameObject);
        }

        /// <summary>Formats the counts that say what a mesh now is, for a verb's confirmation line.</summary>
        /// <param name="mesh">Mesh to describe.</param>
        /// <returns>A compact counts string.</returns>
        private static string DescribeProBuilderMesh(ProBuilderMesh mesh)
        {
            return "faces=" + mesh.faceCount.ToString(CultureInfo.InvariantCulture)
                + " verts=" + mesh.vertexCount.ToString(CultureInfo.InvariantCulture)
                + " tris=" + mesh.triangleCount.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Reads an optional float from an options object, reporting a malformed value.</summary>
        /// <param name="options">Options object, or null.</param>
        /// <param name="key">Property name.</param>
        /// <param name="fallback">Value to use when absent.</param>
        /// <returns>The value, or the fallback.</returns>
        private static float PbFloat(JObject options, string key, float fallback)
        {
            if (options == null) return fallback;
            var token = options[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            return (float)token;
        }

        /// <summary>Reads an optional int from an options object.</summary>
        /// <param name="options">Options object, or null.</param>
        /// <param name="key">Property name.</param>
        /// <param name="fallback">Value to use when absent.</param>
        /// <returns>The value, or the fallback.</returns>
        private static int PbInt(JObject options, string key, int fallback)
        {
            if (options == null) return fallback;
            var token = options[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            return (int)token;
        }

        /// <summary>Reads an optional bool from an options object.</summary>
        /// <param name="options">Options object, or null.</param>
        /// <param name="key">Property name.</param>
        /// <param name="fallback">Value to use when absent.</param>
        /// <returns>The value, or the fallback.</returns>
        private static bool PbBool(JObject options, string key, bool fallback)
        {
            if (options == null) return fallback;
            var token = options[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            return (bool)token;
        }

        /// <summary>Reads an optional Vector3 written as three numbers, e.g. "1 2 3".</summary>
        /// <param name="options">Options object, or null.</param>
        /// <param name="key">Property name.</param>
        /// <param name="fallback">Value to use when absent or unparseable.</param>
        /// <returns>The value, or the fallback.</returns>
        private static Vector3 PbVector3(JObject options, string key, Vector3 fallback)
        {
            if (options == null) return fallback;
            var token = options[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;

            Vector3 parsed;
            return TryParseVector3(Convert.ToString(token), out parsed) ? parsed : fallback;
        }

        /// <summary>
        /// Parses one polygon ring written as "x y z, x y z, ...", rejecting anything that cannot form a
        /// face so a malformed corner is named rather than turned into degenerate geometry.
        /// </summary>
        /// <param name="text">Comma separated triples.</param>
        /// <param name="label">What this ring is, so an error says which one was wrong.</param>
        /// <param name="points">Receives the parsed corners.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string TryParsePolygonPoints(
            string text, string label, int minimum, out List<Vector3> points)
        {
            points = new List<Vector3>();

            foreach (var token in (text ?? string.Empty).Split(','))
            {
                var trimmed = token.Trim();
                if (trimmed.Length == 0) continue;

                Vector3 point;
                if (!TryParseVector3(trimmed, out point))
                {
                    return "'" + trimmed + "' in the " + label + " is not three numbers. Write each point "
                        + "as 'x y z' and separate the points with commas.";
                }

                points.Add(point);
            }

            if (points.Count < minimum)
            {
                return "The " + label + " has " + points.Count.ToString(CultureInfo.InvariantCulture)
                    + " point(s), and this needs at least " + minimum.ToString(CultureInfo.InvariantCulture)
                    + ".";
            }

            return null;
        }

        #endregion

        #region Creating geometry

        /// <summary>
        /// Builds one parametric ProBuilder shape in the scene named by the required scene argument, reporting
        /// what it produced rather than that it produced something. A parent given in the options must be in
        /// that same scene.
        /// </summary>
        /// <param name="args">Scene, shape name, then an optional JSON options object.</param>
        /// <returns>What was created with its counts, or a validation error.</returns>
        private static string ProBuilderShapeCreateCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            UnityEngine.SceneManagement.Scene scene;
            var sceneError = TryResolveRequiredScene(sections.Length > 0 ? sections[0] : string.Empty, out scene);
            if (sceneError != null) return sceneError;

            var shape = sections.Length > 1 ? sections[1].Trim().ToLowerInvariant() : string.Empty;
            if (shape.Length == 0)
            {
                return "Usage: probuilder-shape-create <scene> | <shape> [| optionsJson]. Shapes: cube, plane, "
                    + "cylinder, prism, stair, curved-stair, door, pipe, cone, arch, sphere, torus, sprite.";
            }

            JObject options = null;
            if (sections.Length > 2 && sections[2].Trim().Length > 0)
            {
                try { options = JObject.Parse(sections[2]); }
                catch (Exception parseError)
                {
                    return "The options payload is not a JSON object: " + parseError.Message;
                }
            }

            var pivot = string.Equals(
                Convert.ToString(options == null ? null : options["pivot"]), "corner",
                StringComparison.OrdinalIgnoreCase)
                ? PivotLocation.FirstCorner
                : PivotLocation.Center;

            ProBuilderMesh mesh;
            switch (shape)
            {
                case "cube":
                    mesh = ShapeGenerator.GenerateCube(pivot, PbVector3(options, "size", Vector3.one));
                    break;

                case "plane":
                    mesh = ShapeGenerator.GeneratePlane(
                        pivot, PbFloat(options, "width", 1f), PbFloat(options, "height", 1f),
                        PbInt(options, "widthCuts", 0), PbInt(options, "heightCuts", 0), Axis.Up);
                    break;

                case "cylinder":
                    mesh = ShapeGenerator.GenerateCylinder(
                        pivot, PbInt(options, "sides", 8), PbFloat(options, "radius", 0.5f),
                        PbFloat(options, "height", 2f), PbInt(options, "heightCuts", 0),
                        PbInt(options, "smoothing", -1));
                    break;

                case "prism":
                    mesh = ShapeGenerator.GeneratePrism(pivot, PbVector3(options, "size", Vector3.one));
                    break;

                case "stair":
                    mesh = ShapeGenerator.GenerateStair(
                        pivot, PbVector3(options, "size", new Vector3(2f, 2.5f, 4f)),
                        PbInt(options, "steps", 6), PbBool(options, "buildSides", true));
                    break;

                case "curved-stair":
                    mesh = ShapeGenerator.GenerateCurvedStair(
                        pivot, PbFloat(options, "width", 2f), PbFloat(options, "height", 2.5f),
                        PbFloat(options, "innerRadius", 2f), PbFloat(options, "circumference", 180f),
                        PbInt(options, "steps", 8), PbBool(options, "buildSides", true));
                    break;

                case "door":
                    mesh = ShapeGenerator.GenerateDoor(
                        pivot, PbFloat(options, "width", 4f), PbFloat(options, "height", 4f),
                        PbFloat(options, "ledgeHeight", 0.75f), PbFloat(options, "legWidth", 1f),
                        PbFloat(options, "depth", 0.5f));
                    break;

                case "pipe":
                    mesh = ShapeGenerator.GeneratePipe(
                        pivot, PbFloat(options, "radius", 0.5f), PbFloat(options, "height", 2f),
                        PbFloat(options, "thickness", 0.25f), PbInt(options, "sides", 8),
                        PbInt(options, "heightCuts", 0));
                    break;

                case "cone":
                    mesh = ShapeGenerator.GenerateCone(
                        pivot, PbFloat(options, "radius", 0.5f), PbFloat(options, "height", 1f),
                        PbInt(options, "sides", 8));
                    break;

                case "arch":
                    mesh = ShapeGenerator.GenerateArch(
                        pivot, PbFloat(options, "angle", 180f), PbFloat(options, "radius", 3f),
                        PbFloat(options, "width", 0.5f), PbFloat(options, "depth", 1f),
                        PbInt(options, "radialCuts", 6), PbBool(options, "insideFaces", true),
                        PbBool(options, "outsideFaces", true), PbBool(options, "frontFaces", true),
                        PbBool(options, "backFaces", true), PbBool(options, "endCaps", true));
                    break;

                case "sphere":
                    mesh = ShapeGenerator.GenerateIcosahedron(
                        pivot, PbFloat(options, "radius", 0.5f), PbInt(options, "subdivisions", 2),
                        PbBool(options, "weld", true), false);
                    break;

                case "torus":
                    mesh = ShapeGenerator.GenerateTorus(
                        pivot, PbInt(options, "rows", 12), PbInt(options, "columns", 16),
                        PbFloat(options, "innerRadius", 1f), PbFloat(options, "outerRadius", 0.3f),
                        PbBool(options, "smooth", true), 360f, 360f, false);
                    break;

                // A sprite is a fixed one-unit quad with no parameters of its own, so it comes from the
                // shape-type factory rather than a generator taking dimensions.
                case "sprite":
                    mesh = ShapeGenerator.CreateShape(ShapeType.Sprite, pivot);
                    break;

                default:
                    return "'" + shape + "' is not a shape this verb builds. Shapes: cube, plane, cylinder, "
                        + "prism, stair, curved-stair, door, pipe, cone, arch, sphere, torus, sprite.";
            }

            if (mesh == null)
            {
                return "ProBuilder returned no mesh for '" + shape + "'.";
            }

            var name = Convert.ToString(options == null ? null : options["name"]);
            if (!string.IsNullOrEmpty(name)) mesh.gameObject.name = name;

            var parentTarget = Convert.ToString(options == null ? null : options["parent"]);
            if (!string.IsNullOrEmpty(parentTarget) && !IsRootTarget(parentTarget))
            {
                GameObject parent;
                var parentError = TryFindGameObject(parentTarget, out parent);
                if (parentError == null) parentError = CheckParentInScene(parent, scene);
                if (parentError != null)
                {
                    UnityEngine.Object.DestroyImmediate(mesh.gameObject);
                    return parentError;
                }

                mesh.transform.SetParent(parent.transform, false);
            }
            else
            {
                PlaceRootInScene(mesh.gameObject, scene);
            }

            Vector3 position;
            if (options != null && TryParseVector3(Convert.ToString(options["position"]), out position))
            {
                mesh.transform.localPosition = position;
            }

            // ShapeGenerator leaves the renderer's material NULL, which renders magenta - ProBuilder's own
            // New Shape tool assigns one as a separate step. Measured on a generated cube: m_Materials was
            // [null]. A caller's own material wins; otherwise the shape gets ProBuilder's default so it is
            // visible rather than shipping as an error colour.
            var materialPath = Convert.ToString(options == null ? null : options["material"]);
            Material material = null;
            if (!string.IsNullOrEmpty(materialPath))
            {
                material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    UnityEngine.Object.DestroyImmediate(mesh.gameObject);
                    return "No material asset at '" + materialPath + "', so nothing was created. "
                        + "assets-find with t:Material lists the ones this project has.";
                }
            }

            var renderer = mesh.GetComponent<MeshRenderer>();
            var appliedMaterial = material != null ? material : BuiltinMaterials.defaultMaterial;
            if (renderer != null && appliedMaterial != null) renderer.sharedMaterial = appliedMaterial;

            // The generator leaves the static flags clear, and without ContributeGI the shape is simply
            // ABSENT from a lightmap bake - it still renders, so nothing reports that it was skipped and
            // the fault looks like a lighting problem rather than a flag. World geometry is marked static
            // here for that reason; pass "static": false for anything that moves at runtime.
            var markStatic = PbBool(options, "static", true);
            if (markStatic)
            {
                GameObjectUtility.SetStaticEditorFlags(
                    mesh.gameObject,
                    StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic
                    | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic
                    | StaticEditorFlags.ReflectionProbeStatic);
            }

            RebuildProBuilderMesh(mesh);
            RegisterCreatedUndo(mesh.gameObject, "Conduit Create ProBuilder Shape");

            return "Created " + shape + ": " + GetGameObjectScenePath(mesh.gameObject)
                + " [" + mesh.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + "]  "
                + DescribeProBuilderMesh(mesh)
                + "  material: " + (appliedMaterial == null ? "NONE (renders magenta)" : appliedMaterial.name)
                + (markStatic
                    ? "  static: ContributeGI, Batching, Occluder, Occludee, ReflectionProbe - it will bake"
                    : "  static: none - it will NOT contribute to a lightmap bake");
        }

        /// <summary>
        /// Builds a shape from a drawn footprint and an extrude depth, which is how a floor plan becomes
        /// geometry, including holes cut clean through it for doorways and windows.
        /// </summary>
        /// <param name="args">Scene, object name, then the outline corners, then an optional JSON options object. The object lands at the root of that scene.</param>
        /// <returns>What was created with its counts, or a validation error.</returns>
        private static string ProBuilderPolyCreateCommand(string args)
        {
            // The footprint rides in a PIPE section rather than a token slot: a token argument carrying
            // spaces and commas is quoted on its way in, and those quotes arrive as part of the value.
            var sections = SplitCommandSections(args ?? string.Empty);
            UnityEngine.SceneManagement.Scene scene;
            var sceneError = TryResolveRequiredScene(sections.Length > 0 ? sections[0] : string.Empty, out scene);
            if (sceneError != null) return sceneError;

            var nameTokens = sections.Length > 1 ? SplitCommandTokens(sections[1], false) : new string[0];
            var outline = sections.Length > 2 ? sections[2].Trim() : string.Empty;
            if (nameTokens.Length == 0 || outline.Length == 0)
            {
                return "Usage: probuilder-poly-create <scene> | <name> | <points> [| optionsJson]. points is the "
                    + "footprint as 'x y z, x y z, ...' with three or more corners, and it goes after the "
                    + "pipe because it carries spaces and commas. Options: extrude, flipNormals, holes (an "
                    + "array of point strings in that same grammar), material, static.";
            }

            var name = nameTokens[0];

            List<Vector3> points;
            var error = TryParsePolygonPoints(outline, "footprint", 3, out points);
            if (error != null) return error;

            JObject options = null;
            if (sections.Length > 3 && sections[3].Trim().Length > 0)
            {
                try { options = JObject.Parse(sections[3]); }
                catch (Exception parseError)
                {
                    return "The options payload is not a JSON object: " + parseError.Message;
                }
            }

            // Every hole is parsed before anything is created, so a typo in the third one cannot leave a
            // half-built object in the scene for the caller to find and tidy up.
            var holes = new List<IList<Vector3>>();
            var holesToken = options == null ? null : options["holes"];
            if (holesToken != null && holesToken.Type != JTokenType.Null)
            {
                var holeNumber = 1;
                foreach (var entry in holesToken)
                {
                    List<Vector3> holePoints;
                    error = TryParsePolygonPoints(
                        Convert.ToString(entry),
                        "hole " + holeNumber.ToString(CultureInfo.InvariantCulture),
                        3, out holePoints);
                    if (error != null) return error;

                    holes.Add(holePoints);
                    holeNumber++;
                }
            }

            Material material = null;
            var materialPath = Convert.ToString(options == null ? null : options["material"]);
            if (!string.IsNullOrEmpty(materialPath))
            {
                material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    return "No material asset at '" + materialPath + "', so nothing was created. "
                        + "assets-find with t:Material lists the ones this project has.";
                }
            }

            var extrude = PbFloat(options, "extrude", 1f);
            var flipNormals = PbBool(options, "flipNormals", false);

            var gameObject = new GameObject(name);
            PlaceRootInScene(gameObject, scene);
            var mesh = gameObject.AddComponent<ProBuilderMesh>();

            ActionResult result;
            try
            {
                result = holes.Count > 0
                    ? AppendElements.CreateShapeFromPolygon(mesh, points, extrude, flipNormals, holes)
                    : AppendElements.CreateShapeFromPolygon(mesh, points, extrude, flipNormals);
            }
            catch (Exception shapeError)
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                return "ProBuilder could not build that polygon: " + shapeError.Message
                    + ". Nothing was created.";
            }

            if (!result.ToBool())
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                return "ProBuilder refused that footprint: " + result.notification
                    + ". Corners must not be collinear or self-intersecting, and every hole must sit "
                    + "entirely inside the outline. Nothing was created.";
            }

            var renderer = mesh.GetComponent<MeshRenderer>();
            var appliedMaterial = material != null ? material : BuiltinMaterials.defaultMaterial;
            if (renderer != null && appliedMaterial != null) renderer.sharedMaterial = appliedMaterial;

            var markStatic = PbBool(options, "static", true);
            if (markStatic)
            {
                GameObjectUtility.SetStaticEditorFlags(
                    gameObject,
                    StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic
                    | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic
                    | StaticEditorFlags.ReflectionProbeStatic);
            }

            RebuildProBuilderMesh(mesh);
            RegisterCreatedUndo(gameObject, "Conduit Create ProBuilder Polygon");

            return "Created polygon: " + GetGameObjectScenePath(gameObject)
                + " [" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + "]  "
                + DescribeProBuilderMesh(mesh)
                + "  from " + points.Count.ToString(CultureInfo.InvariantCulture) + " corners"
                + (holes.Count > 0
                    ? " with " + holes.Count.ToString(CultureInfo.InvariantCulture) + " hole(s) through it"
                    : " and no holes")
                + ", extruded " + extrude.ToString("0.###", CultureInfo.InvariantCulture)
                + "  material: " + (appliedMaterial == null ? "NONE (renders magenta)" : appliedMaterial.name)
                + (markStatic
                    ? "  static: ContributeGI, Batching, Occluder, Occludee, ReflectionProbe - it will bake"
                    : "  static: none - it will NOT contribute to a lightmap bake")
                + ". Lightmap UVs are NOT generated by this: run probuilder-lightmap-uvs before baking.";
        }

        /// <summary>
        /// Sweeps a tube along a bezier spline through the given points, which is ProBuilder's Bezier
        /// Shape. Unlike the Cut tool, nothing here is modal: the shape is a plain MonoBehaviour whose
        /// points and dimensions are fields and whose Refresh rebuilds the mesh, so the whole thing is
        /// parameters.
        /// </summary>
        /// <param name="args">Scene, name, the spline points, then optional settings. A parent given in the settings must be in that scene.</param>
        /// <returns>What was created, or a validation error.</returns>
        private static string ProBuilderBezierCreateCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            UnityEngine.SceneManagement.Scene scene;
            var sceneError = TryResolveRequiredScene(sections.Length > 0 ? sections[0] : string.Empty, out scene);
            if (sceneError != null) return sceneError;

            var nameTokens = sections.Length > 1 ? SplitCommandTokens(sections[1], false) : new string[0];
            var spline = sections.Length > 2 ? sections[2].Trim() : string.Empty;
            if (nameTokens.Length == 0 || spline.Length == 0)
            {
                return "Usage: probuilder-bezier-create <scene> | <name> | <points> [| optionsJson]. points is the "
                    + "spline as 'x y z, x y z, ...' with two or more points in LOCAL space, and it goes "
                    + "after the pipe because it carries spaces and commas. Options: radius, rows, "
                    + "columns, smooth, closeLoop, material, static, parent, position.";
            }

            var name = nameTokens[0];

            List<Vector3> points;
            var error = TryParsePolygonPoints(spline, "spline", 2, out points);
            if (error != null) return error;

            JObject options = null;
            if (sections.Length > 3 && sections[3].Trim().Length > 0)
            {
                try { options = JObject.Parse(sections[3]); }
                catch (Exception parseError)
                {
                    return "The options payload is not a JSON object: " + parseError.Message;
                }
            }

            // Both types are internal to Unity.ProBuilder, so they are reached by name rather than
            // referenced - the same way probuilder-fit-to-size reaches ProBuilderShape.
            var shapeType = LoadedTypeByName("UnityEngine.ProBuilder.BezierShape");
            var pointType = LoadedTypeByName("UnityEngine.ProBuilder.BezierPoint");
            if (shapeType == null || pointType == null)
            {
                return "This ProBuilder build does not ship its bezier shape, so this verb is unavailable "
                    + "here. probuilder-poly-create builds a swept shape from a footprint instead.";
            }

            var radius = PbFloat(options, "radius", 0.5f);
            var rows = PbInt(options, "rows", 8);
            var columns = PbInt(options, "columns", 16);
            var smooth = PbBool(options, "smooth", true);
            var closeLoop = PbBool(options, "closeLoop", false);

            if (radius <= 0f) return "radius must be greater than 0, so nothing was created.";
            if (rows < 1) return "rows must be at least 1, so nothing was created.";
            if (columns < 3)
            {
                return "columns must be at least 3 - fewer than three sides around the tube cannot enclose "
                    + "anything. Nothing was created.";
            }

            var materialPath = Convert.ToString(options == null ? null : options["material"]);
            Material material = null;
            if (!string.IsNullOrEmpty(materialPath))
            {
                material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    return "No material asset at '" + materialPath + "', so nothing was created. "
                        + "assets-find with t:Material lists the ones this project has.";
                }
            }

            var gameObject = new GameObject(name);

            var parentTarget = Convert.ToString(options == null ? null : options["parent"]);
            if (!string.IsNullOrEmpty(parentTarget) && !IsRootTarget(parentTarget))
            {
                GameObject parent;
                var parentError = TryFindGameObject(parentTarget, out parent);
                if (parentError == null) parentError = CheckParentInScene(parent, scene);
                if (parentError != null)
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                    return parentError;
                }

                gameObject.transform.SetParent(parent.transform, false);
            }
            else
            {
                PlaceRootInScene(gameObject, scene);
            }

            Vector3 position;
            if (options != null && TryParseVector3(Convert.ToString(options["position"]), out position))
            {
                gameObject.transform.localPosition = position;
            }

            // BezierShape.mesh only LOOKS the component up, it never adds one, so Refresh would dereference
            // null on a bare object. Measured in BezierShape.cs: the getter is GetComponent and nothing else.
            var mesh = gameObject.AddComponent<ProBuilderMesh>();
            var shape = gameObject.AddComponent(shapeType);

            try
            {
                error = ApplyBezierSpline(shape, shapeType, pointType, points, closeLoop);
                if (error != null)
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                    return error;
                }

                SetProBuilderField(shape, shapeType, "radius", radius);
                SetProBuilderField(shape, shapeType, "rows", rows);
                SetProBuilderField(shape, shapeType, "columns", columns);
                SetProBuilderField(shape, shapeType, "smooth", smooth);
                SetProBuilderField(shape, shapeType, "closeLoop", closeLoop);

                var refresh = shapeType.GetMethod("Refresh", INSTANCE_MEMBERS);
                if (refresh == null)
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                    return "ProBuilder's bezier shape has no Refresh in this build, so the mesh could not "
                        + "be built. Nothing was created.";
                }

                refresh.Invoke(shape, null);
            }
            catch (Exception bezierError)
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                return "ProBuilder could not sweep that spline: " + Unwrap(bezierError)
                    + ". Nothing was created.";
            }

            if (mesh.faceCount == 0)
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                return "ProBuilder swept no geometry from those "
                    + points.Count.ToString(CultureInfo.InvariantCulture)
                    + " points, so nothing was created rather than leaving an empty object behind.";
            }

            var renderer = mesh.GetComponent<MeshRenderer>();
            var appliedMaterial = material != null ? material : BuiltinMaterials.defaultMaterial;
            if (renderer != null && appliedMaterial != null) renderer.sharedMaterial = appliedMaterial;

            var markStatic = PbBool(options, "static", true);
            if (markStatic)
            {
                GameObjectUtility.SetStaticEditorFlags(
                    gameObject,
                    StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic
                    | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic
                    | StaticEditorFlags.ReflectionProbeStatic);
            }

            RebuildProBuilderMesh(mesh);
            RegisterCreatedUndo(gameObject, "Conduit Create ProBuilder Bezier");

            return "Created bezier sweep: " + GetGameObjectScenePath(gameObject)
                + " [" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + "]  "
                + DescribeProBuilderMesh(mesh)
                + "  through " + points.Count.ToString(CultureInfo.InvariantCulture) + " point(s)"
                + (closeLoop ? " as a CLOSED loop" : " open-ended")
                + ", radius " + radius.ToString("0.###", CultureInfo.InvariantCulture)
                + ", " + columns.ToString(CultureInfo.InvariantCulture) + " around by "
                + rows.ToString(CultureInfo.InvariantCulture) + " along, "
                + (smooth ? "smooth-shaded" : "hard-shaded")
                + "  material: " + (appliedMaterial == null ? "NONE (renders magenta)" : appliedMaterial.name)
                + (markStatic
                    ? "  static: ContributeGI, Batching, Occluder, Occludee, ReflectionProbe - it will bake"
                    : "  static: none - it will NOT contribute to a lightmap bake")
                + ". Tangents were computed to pass the curve THROUGH every point; the BezierShape "
                + "component keeps them, so they can be adjusted per point with gameobject-component-get "
                + "and -modify. Lightmap UVs are NOT generated by this: run probuilder-lightmap-uvs before "
                + "baking.";
        }

        /// <summary>
        /// Fills a bezier shape's point list, computing each point's tangents so the curve passes THROUGH
        /// the positions given rather than merely near them.
        /// </summary>
        /// <param name="shape">The BezierShape component.</param>
        /// <param name="shapeType">Its type.</param>
        /// <param name="pointType">The BezierPoint type.</param>
        /// <param name="points">Positions the curve must pass through.</param>
        /// <param name="closeLoop">Whether the spline closes, which changes the end tangents.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string ApplyBezierSpline(
            Component shape, Type shapeType, Type pointType, List<Vector3> points, bool closeLoop)
        {
            var field = shapeType.GetField("points", INSTANCE_MEMBERS);
            if (field == null) return "ProBuilder's bezier shape has no points list in this build.";

            var list = field.GetValue(shape);
            if (list == null) return "ProBuilder's bezier shape returned no points list.";

            var listType = list.GetType();
            listType.GetMethod("Clear").Invoke(list, null);
            var add = listType.GetMethod("Add");

            for (var index = 0; index < points.Count; index++)
            {
                // A Catmull-Rom tangent, which is what makes the curve pass through its control points:
                // the handle at each point runs parallel to the line between its neighbours, a sixth of
                // that span long. An open end mirrors its one neighbour so the curve does not kink there.
                var previous = index > 0
                    ? points[index - 1]
                    : closeLoop ? points[points.Count - 1] : points[index] - (points[index + 1] - points[index]);
                var next = index < points.Count - 1
                    ? points[index + 1]
                    : closeLoop ? points[0] : points[index] + (points[index] - points[index - 1]);

                // Stored ABSOLUTE rather than as an offset, which is the form ProBuilder's own Init writes
                // (it seeds the second point's tangents as p1 + tan and p1 - tan, not as bare tan).
                var handle = (next - previous) / 6f;
                add.Invoke(
                    list,
                    new[]
                    {
                        Activator.CreateInstance(
                            pointType,
                            new object[]
                            {
                                points[index], points[index] - handle, points[index] + handle,
                                Quaternion.identity,
                            }),
                    });
            }

            return null;
        }

        /// <summary>Writes one public field on a component whose type is internal to ProBuilder.</summary>
        /// <param name="target">Component to write to.</param>
        /// <param name="type">Its type.</param>
        /// <param name="field">Field name.</param>
        /// <param name="value">Value to write.</param>
        private static void SetProBuilderField(Component target, Type type, string field, object value)
        {
            var found = type.GetField(field, INSTANCE_MEMBERS);
            if (found != null) found.SetValue(target, value);
        }

        /// <summary>
        /// Converts an ordinary mesh into editable ProBuilder geometry in place, which is how an imported
        /// model or a Unity primitive becomes something the probuilder-* verbs can address.
        /// </summary>
        /// <param name="args">Target, then an optional JSON options object.</param>
        /// <returns>What was converted with its counts, or a validation error.</returns>
        private static string ProBuilderizeCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            var target = sections.Length > 0 ? sections[0].Trim() : string.Empty;
            if (target.Length == 0)
            {
                return "Usage: probuilder-ize <target> [| optionsJson]. Options: quads, smoothing, "
                    + "smoothingAngle.";
            }

            GameObject gameObject;
            var error = TryFindGameObject(target, out gameObject);
            if (error != null) return error;

            if (gameObject.GetComponent<ProBuilderMesh>() != null)
            {
                return "'" + GetGameObjectPath(gameObject) + "' is ALREADY ProBuilder geometry. Importing "
                    + "over it would discard the face and UV work it carries, so nothing was changed.";
            }

            var filter = gameObject.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                return "'" + GetGameObjectPath(gameObject) + "' has no MeshFilter holding a mesh, so there "
                    + "is nothing to convert. A SkinnedMeshRenderer cannot be converted this way.";
            }

            // The importer throws on a topology it cannot read, and by then it has already added its own
            // component - so topology is checked HERE, while refusing still leaves the object untouched.
            var source = filter.sharedMesh;
            for (var submesh = 0; submesh < source.subMeshCount; submesh++)
            {
                var topology = source.GetTopology(submesh);
                if (topology == MeshTopology.Triangles || topology == MeshTopology.Quads) continue;

                return "Submesh " + submesh.ToString(CultureInfo.InvariantCulture) + " of '"
                    + GetGameObjectPath(gameObject) + "' is " + topology + " topology, and ProBuilder reads "
                    + "triangles and quads only. Nothing was changed.";
            }

            JObject options = null;
            if (sections.Length > 1 && sections[1].Trim().Length > 0)
            {
                try { options = JObject.Parse(sections[1]); }
                catch (Exception parseError)
                {
                    return "The options payload is not a JSON object: " + parseError.Message;
                }
            }

            var settings = new MeshImportSettings
            {
                quads = PbBool(options, "quads", true),
                smoothing = PbBool(options, "smoothing", true),
                smoothingAngle = PbFloat(options, "smoothingAngle", 1f)
            };

            var sourceName = source.name;
            var sourceTriangles = source.triangles.Length / 3;
            var hadLightmapUvs = source.uv2 != null && source.uv2.Length > 0;
            var sourceRenderer = gameObject.GetComponent<MeshRenderer>();
            var sourceMaterials = sourceRenderer == null ? null : sourceRenderer.sharedMaterials;

            RecordUndo(gameObject, "Conduit ProBuilderize");

            // The importer's GameObject constructor re-reads MeshFilter.sharedMesh AFTER attaching its own
            // component, and attaching it takes the filter over - so that read returns null and the import
            // throws on geometry that is plainly there. Measured: the guard above saw a mesh, and with only
            // the AddComponent between them the constructor did not. The mesh captured above is handed over
            // explicitly instead, through the constructor that accepts it.
            var mesh = Undo.AddComponent<ProBuilderMesh>(gameObject);

            try
            {
                new MeshImporter(source, sourceMaterials, mesh).Import(settings);
            }
            catch (Exception importError)
            {
                Undo.DestroyObjectImmediate(mesh);
                return "ProBuilder could not import '" + sourceName + "': " + importError.Message
                    + ". The object was left exactly as it was.";
            }

            RebuildProBuilderMesh(mesh);

            return "Converted " + GetGameObjectPath(gameObject) + " from the ordinary mesh '" + sourceName
                + "' (" + sourceTriangles.ToString(CultureInfo.InvariantCulture)
                + " triangles) into editable ProBuilder geometry: " + DescribeProBuilderMesh(mesh)
                + ". quads=" + (settings.quads ? "yes" : "no")
                + ", smoothing=" + (settings.smoothing
                    ? "yes above " + settings.smoothingAngle.ToString("0.###", CultureInfo.InvariantCulture)
                        + " degrees"
                    : "no")
                + ". The MeshFilter now renders a NEW mesh and the source asset is untouched"
                + (hadLightmapUvs
                    ? ", but its lightmap UVs did NOT survive the import - run probuilder-lightmap-uvs before "
                        + "baking."
                    : ", and it has no lightmap UVs - run probuilder-lightmap-uvs before baking.");
        }

        #endregion

        #region Reading geometry

        /// <summary>
        /// Reports a ProBuilder mesh's structure, which is what every other verb's face indexes address.
        /// </summary>
        /// <param name="args">Target, then an optional section.</param>
        /// <returns>The report, or a validation error.</returns>
        private static string ProBuilderMeshGetCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            var target = sections.Length > 0 ? sections[0].Trim() : string.Empty;
            if (target.Length == 0)
            {
                return "Usage: probuilder-mesh-get <target> [| section]. Sections: summary, faces, "
                    + "selection, colors.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(target, out mesh);
            if (error != null) return error;

            var section = sections.Length > 1 ? sections[1].Trim().ToLowerInvariant() : "summary";
            if (section.Length == 0) section = "summary";

            var report = new StringBuilder();
            report.Append(GetGameObjectPath(mesh.gameObject)).Append("  ").Append(DescribeProBuilderMesh(mesh));
            report.Append(Environment.NewLine).Append("selected: faces=")
                .Append(mesh.selectedFaceCount.ToString(CultureInfo.InvariantCulture))
                .Append(" verts=").Append(mesh.selectedVertexCount.ToString(CultureInfo.InvariantCulture))
                .Append(" edges=").Append(mesh.selectedEdgeCount.ToString(CultureInfo.InvariantCulture));

            if (section == "summary") return report.ToString();

            if (section == "selection")
            {
                report.Append(Environment.NewLine).Append("selectedFaceIndexes: ");
                if (mesh.selectedFaceCount == 0) report.Append("(none)");
                else
                {
                    var first = true;
                    foreach (var index in mesh.selectedFaceIndexes)
                    {
                        if (!first) report.Append(", ");
                        first = false;
                        report.Append(index.ToString(CultureInfo.InvariantCulture));
                    }
                }

                return report.ToString();
            }

            if (section == "colors") return AppendProBuilderVertexColors(mesh, report);

            if (section != "faces")
            {
                return "'" + section + "' is not a section of this report. Sections: summary, faces, "
                    + "selection, colors.";
            }

            for (var index = 0; index < mesh.faceCount; index++)
            {
                var face = mesh.faces[index];
                report.Append(Environment.NewLine).Append("  face ")
                    .Append(index.ToString(CultureInfo.InvariantCulture))
                    .Append(": indexes=").Append(face.indexes.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(" submesh=").Append(face.submeshIndex.ToString(CultureInfo.InvariantCulture))
                    .Append(" smoothing=").Append(face.smoothingGroup.ToString(CultureInfo.InvariantCulture))
                    .Append(" manualUV=").Append(face.manualUV)
                    .Append(" uvScale=").Append(face.uv.scale.ToString())
                    .Append(" uvOffset=").Append(face.uv.offset.ToString())
                    .Append(" uvRotation=")
                    .Append(face.uv.rotation.ToString("0.##", CultureInfo.InvariantCulture));
            }

            return report.ToString();
        }

        /// <summary>
        /// Reports which vertex colours a mesh carries, face by face. Colours come back in the form the
        /// setter TAKES rather than the form stored: in a linear colour space project the stored value is
        /// the linear one, so it is converted back before printing, and a colour read here can be written
        /// straight back without arriving several shades darker.
        /// </summary>
        /// <param name="mesh">Mesh to read.</param>
        /// <param name="report">Report already carrying the summary lines.</param>
        /// <returns>The finished report.</returns>
        private static string AppendProBuilderVertexColors(ProBuilderMesh mesh, StringBuilder report)
        {
            var colors = mesh.colors;
            if (colors == null || colors.Count != mesh.vertexCount)
            {
                report.Append(Environment.NewLine)
                    .Append("vertexColors: NONE - this mesh carries no colour array, which is not the same ")
                    .Append("as one full of white. probuilder-vertex-color-set assigns them.");
                return report.ToString();
            }

            var linear = PlayerSettings.colorSpace == ColorSpace.Linear;
            var distinct = new HashSet<uint>();
            for (var index = 0; index < colors.Count; index++) distinct.Add(PackColor32(colors[index]));

            report.Append(Environment.NewLine).Append("vertexColors: ")
                .Append(distinct.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" distinct across ").Append(colors.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" vertices, written as probuilder-vertex-color-set takes them")
                .Append(linear ? " (converted back out of this project's LINEAR storage)." : ".");

            for (var index = 0; index < mesh.faceCount; index++)
            {
                var onFace = new List<string>();
                var seen = new HashSet<uint>();
                foreach (var vertex in mesh.faces[index].distinctIndexes)
                {
                    if (!seen.Add(PackColor32(colors[vertex]))) continue;

                    onFace.Add(FormatColor(linear ? colors[vertex].gamma : colors[vertex]));
                }

                report.Append(Environment.NewLine).Append("  face ")
                    .Append(index.ToString(CultureInfo.InvariantCulture)).Append(": ")
                    .Append(string.Join(", ", onFace.ToArray()))
                    .Append(onFace.Count > 1 ? "  (more than one, so this face is a gradient)" : string.Empty);
            }

            return report.ToString();
        }

        #endregion

        #region Selection

        /// <summary>
        /// Sets which faces a mesh has selected, so the face operations can act on "selected" the way a
        /// person working in the editor would.
        /// </summary>
        /// <param name="args">Target, then a face selector.</param>
        /// <returns>What is now selected, or a validation error.</returns>
        private static string ProBuilderSelectCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            var target = sections.Length > 0 ? sections[0].Trim() : string.Empty;
            if (target.Length == 0 || sections.Length < 2)
            {
                return "Usage: probuilder-select <target> | <faces>. faces takes all, none, or indexes "
                    + "separated by commas or spaces.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(target, out mesh);
            if (error != null) return error;

            var selector = sections[1].Trim();
            if (string.Equals(selector, "none", StringComparison.OrdinalIgnoreCase))
            {
                mesh.ClearSelection();
                EditorUtility.SetDirty(mesh);
                return "Cleared the selection on " + GetGameObjectPath(mesh.gameObject) + ".";
            }

            List<Face> faces;
            List<int> indexes;
            error = TryResolveFaces(mesh, selector, out faces, out indexes);
            if (error != null) return error;

            mesh.SetSelectedFaces(faces);
            EditorUtility.SetDirty(mesh);

            return "Selected " + indexes.Count.ToString(CultureInfo.InvariantCulture) + " face(s) on "
                + GetGameObjectPath(mesh.gameObject) + ": " + string.Join(", ", indexes.ConvertAll(
                    index => index.ToString(CultureInfo.InvariantCulture)).ToArray());
        }

        #endregion

        #region Face operations

        /// <summary>Pushes faces out along a normal, which is how a flat shape gains depth.</summary>
        /// <param name="args">Target, distance, then an optional face selector and method.</param>
        /// <returns>What changed with the new counts, or a validation error.</returns>
        private static string ProBuilderFaceExtrudeCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-face-extrude <target> | <distance> [| faces] [| method]. "
                    + "method is vertex-normal (default), face-normal or individual.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            float distance;
            if (!float.TryParse(
                sections[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out distance))
            {
                return "'" + sections[1].Trim() + "' is not a distance.";
            }

            List<Face> faces;
            List<int> indexes;
            error = TryResolveFaces(
                mesh, sections.Length > 2 ? sections[2] : string.Empty, out faces, out indexes);
            if (error != null) return error;

            // Vertex normals, matching the Extrude Faces action's own documented default, so a caller who
            // read ProBuilder's manual gets the extrusion that page describes.
            var methodName = sections.Length > 3 ? sections[3].Trim().ToLowerInvariant() : "vertex-normal";
            if (methodName.Length == 0) methodName = "vertex-normal";

            ExtrudeMethod method;
            switch (methodName)
            {
                case "face-normal": method = ExtrudeMethod.FaceNormal; break;
                case "vertex-normal": method = ExtrudeMethod.VertexNormal; break;
                case "individual": method = ExtrudeMethod.IndividualFaces; break;
                default:
                    return "'" + methodName + "' is not an extrude method. Use face-normal, vertex-normal "
                        + "or individual.";
            }

            var beforeFaces = mesh.faceCount;
            var beforeVerts = mesh.vertexCount;

            RecordUndo(mesh, "Conduit Extrude Faces");
            Face[] created = ExtrudeElements.Extrude(mesh, faces, method, distance);
            RebuildProBuilderMesh(mesh);

            if (created == null || created.Length == 0)
            {
                return "Extruded nothing: ProBuilder returned no new faces for those "
                    + indexes.Count.ToString(CultureInfo.InvariantCulture)
                    + " face(s). The mesh is unchanged at " + DescribeProBuilderMesh(mesh) + ".";
            }

            return "Extruded " + indexes.Count.ToString(CultureInfo.InvariantCulture) + " face(s) by "
                + distance.ToString("0.###", CultureInfo.InvariantCulture) + " using " + methodName
                + ", adding " + created.Length.ToString(CultureInfo.InvariantCulture) + " face(s). "
                + "faces " + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture)
                + ", verts " + beforeVerts.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.vertexCount.ToString(CultureInfo.InvariantCulture)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>Removes faces, leaving the mesh open where they were.</summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>What changed, or a validation error.</returns>
        private static string ProBuilderFaceDeleteCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 1 || sections[0].Trim().Length == 0)
            {
                return "Usage: probuilder-face-delete <target> [| faces].";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            List<Face> faces;
            List<int> indexes;
            error = TryResolveFaces(
                mesh, sections.Length > 1 ? sections[1] : string.Empty, out faces, out indexes);
            if (error != null) return error;

            if (faces.Count >= mesh.faceCount)
            {
                return "Refusing to delete every face, which would leave an empty mesh. Delete the object "
                    + "with gameobject-destroy instead.";
            }

            var beforeFaces = mesh.faceCount;

            RecordUndo(mesh, "Conduit Delete Faces");
            DeleteElements.DeleteFaces(mesh, faces);
            RebuildProBuilderMesh(mesh);

            return "Deleted " + indexes.Count.ToString(CultureInfo.InvariantCulture) + " face(s). faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + ", " + DescribeProBuilderMesh(mesh)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>Splits faces into smaller ones, adding the density an edit needs to work with.</summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>What changed, or a validation error.</returns>
        private static string ProBuilderSubdivideCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 1 || sections[0].Trim().Length == 0)
            {
                return "Usage: probuilder-subdivide <target> [| faces].";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            List<Face> faces;
            List<int> indexes;
            error = TryResolveFaces(
                mesh, sections.Length > 1 ? sections[1] : PB_ALL_FACES, out faces, out indexes);
            if (error != null) return error;

            var beforeFaces = mesh.faceCount;

            RecordUndo(mesh, "Conduit Subdivide Faces");
            Face[] created = ConnectElements.Connect(mesh, faces);
            RebuildProBuilderMesh(mesh);

            if (created == null || created.Length == 0)
            {
                return "Subdivided nothing: ProBuilder returned no faces for those "
                    + indexes.Count.ToString(CultureInfo.InvariantCulture) + " face(s). The mesh is "
                    + "unchanged at " + DescribeProBuilderMesh(mesh) + ".";
            }

            return "Subdivided " + indexes.Count.ToString(CultureInfo.InvariantCulture) + " face(s). faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + ", " + DescribeProBuilderMesh(mesh)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        #endregion

        #region Surface and UVs

        /// <summary>Parses two whitespace or comma separated numbers into a Vector2.</summary>
        /// <param name="text">Text to read.</param>
        /// <param name="value">Receives the parsed value.</param>
        /// <returns>True when both numbers parsed.</returns>
        private static bool TryParseVector2(string text, out Vector2 value)
        {
            value = Vector2.zero;
            var parts = (text ?? string.Empty).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;

            float x, y;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;

            value = new Vector2(x, y);
            return true;
        }

        /// <summary>Writes a face's AUTO UV settings, which is the panel a person uses rather than moving UVs by hand.</summary>
        /// <param name="args">Target, a settings object, then an optional face selector.</param>
        /// <returns>What was written per field, or a validation error.</returns>
        private static string ProBuilderUvSetCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-uv-set <target> | <settingsJson> [| faces]. Fields: scale, offset, rotation, fill, anchor, useWorldSpace, flipU, flipV, swapUV.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            JObject settings;
            try { settings = JObject.Parse(sections[1]); }
            catch (Exception parseError)
            {
                return "The settings payload is not a JSON object: " + parseError.Message;
            }

            List<Face> faces;
            List<int> indexes;
            error = TryResolveFaces(mesh, sections.Length > 2 ? sections[2] : string.Empty, out faces, out indexes);
            if (error != null) return error;

            // Every value is validated BEFORE any face is touched, so a bad enum name cannot leave half the
            // selection rewritten and the rest untouched.
            Vector2 scale = Vector2.one;
            var hasScale = settings["scale"] != null;
            if (hasScale && !TryParseVector2(Convert.ToString(settings["scale"]), out scale))
            {
                return "scale must be two numbers, e.g. 2 2.";
            }

            Vector2 offset = Vector2.zero;
            var hasOffset = settings["offset"] != null;
            if (hasOffset && !TryParseVector2(Convert.ToString(settings["offset"]), out offset))
            {
                return "offset must be two numbers, e.g. 0.5 0.";
            }

            AutoUnwrapSettings.Fill fill = AutoUnwrapSettings.Fill.Fit;
            var hasFill = settings["fill"] != null;
            if (hasFill && !Enum.TryParse(Convert.ToString(settings["fill"]), true, out fill))
            {
                return "fill must be one of: " + string.Join(", ", Enum.GetNames(typeof(AutoUnwrapSettings.Fill)));
            }

            AutoUnwrapSettings.Anchor anchor = AutoUnwrapSettings.Anchor.None;
            var hasAnchor = settings["anchor"] != null;
            if (hasAnchor && !Enum.TryParse(Convert.ToString(settings["anchor"]), true, out anchor))
            {
                return "anchor must be one of: " + string.Join(", ", Enum.GetNames(typeof(AutoUnwrapSettings.Anchor)));
            }

            var applied = new List<string>();
            var manualCount = 0;

            RecordUndo(mesh, "Conduit Set ProBuilder UVs");
            foreach (var face in faces)
            {
                // An auto-UV write on a manual-UV face is silently ignored by ProBuilder, so those are
                // counted and reported rather than folded into the applied total.
                if (face.manualUV) { manualCount++; continue; }

                var unwrap = face.uv;
                if (hasScale) unwrap.scale = scale;
                if (hasOffset) unwrap.offset = offset;
                if (settings["rotation"] != null) unwrap.rotation = (float)settings["rotation"];
                if (hasFill) unwrap.fill = fill;
                if (hasAnchor) unwrap.anchor = anchor;
                if (settings["useWorldSpace"] != null) unwrap.useWorldSpace = (bool)settings["useWorldSpace"];
                if (settings["flipU"] != null) unwrap.flipU = (bool)settings["flipU"];
                if (settings["flipV"] != null) unwrap.flipV = (bool)settings["flipV"];
                if (settings["swapUV"] != null) unwrap.swapUV = (bool)settings["swapUV"];
                face.uv = unwrap;
            }

            if (hasScale) applied.Add("scale=" + scale);
            if (hasOffset) applied.Add("offset=" + offset);
            if (settings["rotation"] != null) applied.Add("rotation=" + Convert.ToString(settings["rotation"]));
            if (hasFill) applied.Add("fill=" + fill);
            if (hasAnchor) applied.Add("anchor=" + anchor);
            if (settings["useWorldSpace"] != null) applied.Add("useWorldSpace=" + Convert.ToString(settings["useWorldSpace"]));
            if (settings["flipU"] != null) applied.Add("flipU=" + Convert.ToString(settings["flipU"]));
            if (settings["flipV"] != null) applied.Add("flipV=" + Convert.ToString(settings["flipV"]));
            if (settings["swapUV"] != null) applied.Add("swapUV=" + Convert.ToString(settings["swapUV"]));

            if (applied.Count == 0)
            {
                return "Nothing was written: the settings object named no recognised field. Fields: scale, offset, rotation, fill, anchor, useWorldSpace, flipU, flipV, swapUV.";
            }

            RebuildProBuilderMesh(mesh);

            var written = indexes.Count - manualCount;
            var report = "Set " + string.Join(", ", applied.ToArray()) + " on " + written.ToString(CultureInfo.InvariantCulture) + " face(s).";
            if (manualCount > 0)
            {
                report += " " + manualCount.ToString(CultureInfo.InvariantCulture) + " face(s) were SKIPPED because they use manual UVs, which auto settings do not reach.";
            }

            return report;
        }

        /// <summary>Re-projects a face's UVs from scratch, which is the fix for stretched texturing.</summary>
        /// <param name="args">Target, projection kind, then an optional face selector.</param>
        /// <returns>What was projected, or a validation error.</returns>
        private static string ProBuilderUvProjectCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-uv-project <target> | <projection> [| faces] "
                    + "[| channel:uv0|uv2]. projection is box, sphere or planar.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            var projection = sections[1].Trim().ToLowerInvariant();
            if (projection != "box" && projection != "sphere" && projection != "planar")
            {
                return "'" + projection + "' is not a projection. Use box, sphere or planar.";
            }

            List<Face> faces;
            List<int> indexes;
            error = TryResolveFaces(mesh, sections.Length > 2 ? sections[2] : string.Empty, out faces, out indexes);
            if (error != null) return error;

            int channel;
            error = TryResolveUvChannel(sections.Length > 3 ? sections[3] : string.Empty, out channel);
            if (error != null) return error;

            // UVEditing is INTERNAL to Unity.ProBuilder, so its projection entry points are reached by
            // reflection rather than directly - the same route Conduit uses for PrefabStage. The type is
            // resolved off ProBuilderMesh's own assembly so no name lookup can land on something else, and
            // a missing method is reported BY NAME, which makes an SDK change attributable rather than
            // silent.
            var uvEditing = typeof(ProBuilderMesh).Assembly.GetType(
                "UnityEngine.ProBuilder.MeshOperations.UVEditing");
            if (uvEditing == null)
            {
                return "This ProBuilder build exposes no UVEditing type, so projection is unavailable here. "
                    + "probuilder-uv-set still reaches the auto-UV settings.";
            }

            string methodName;
            object[] callArguments;
            Type[] parameterTypes;
            if (projection == "box" || projection == "planar")
            {
                // Planar and box take the same shape; planar is ProjectFacesAuto, which is what the UV
                // editor's Planar Project runs, and it marks the faces MANUAL afterwards as that action does.
                methodName = projection == "box" ? "ProjectFacesBox" : "ProjectFacesAuto";
                callArguments = new object[] { mesh, faces.ToArray(), channel };
                parameterTypes = new[] { typeof(ProBuilderMesh), typeof(Face[]), typeof(int) };
            }
            else
            {
                var vertexIndexes = new List<int>();
                foreach (var face in faces) vertexIndexes.AddRange(face.distinctIndexes);
                methodName = "ProjectFacesSphere";
                callArguments = new object[] { mesh, vertexIndexes.ToArray(), channel };
                parameterTypes = new[] { typeof(ProBuilderMesh), typeof(int[]), typeof(int) };
            }

            // The overload is named EXACTLY rather than by name alone: ProjectFacesBox ships a second
            // overload taking a lower-left anchor, and a name-only lookup throws AmbiguousMatchException
            // before the projection is ever attempted.
            var projector = uvEditing.GetMethod(
                methodName,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic,
                null,
                parameterTypes,
                null);
            if (projector == null)
            {
                return "This ProBuilder build has no UVEditing." + methodName
                    + ", so that projection is unavailable.";
            }

            RecordUndo(mesh, "Conduit Project ProBuilder UVs");
            try { projector.Invoke(null, callArguments); }
            catch (Exception invokeError)
            {
                return "ProBuilder refused the projection: " + Unwrap(invokeError);
            }

            // UV0 is marked MANUAL because the rebuild REGENERATES an auto face's UV0 from its auto
            // settings, silently discarding the projection just written - ProBuilder's own planar action
            // sets this for the same reason. Measured: without it an exported obj carried identical vt
            // lines either side of the call, while the verb reported success. UV2 is the opposite case:
            // the rebuild's ToMesh exists partly to remove UV2 modifications, so it is skipped there and
            // the manual flag does not apply, since that flag governs UV0 alone.
            if (channel == 0)
            {
                foreach (var projected in faces) projected.manualUV = true;
                RebuildProBuilderMesh(mesh);
            }
            else
            {
                EditorUtility.SetDirty(mesh);
                MarkGameObjectSceneDirty(mesh.gameObject);
            }

            return "Projected " + projection + " " + UvChannelName(channel) + " onto "
                + indexes.Count.ToString(CultureInfo.InvariantCulture)
                + " face(s). Projection writes the coordinates directly, so "
                + (channel == 0
                    ? "those faces now use MANUAL UVs and probuilder-uv-set no longer reaches them."
                    : "probuilder-uv-set is unaffected - it writes UV0 - but a later "
                        + "probuilder-lightmap-uvs regenerates UV2 and would replace this.");
        }

        /// <summary>Assigns a material to faces, which is how one mesh carries several surfaces.</summary>
        /// <param name="args">Target, material asset path, then an optional face selector.</param>
        /// <returns>What was assigned, or a validation error.</returns>
        private static string ProBuilderFaceMaterialCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-face-material <target> | <materialPath> [| faces].";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            var materialPath = sections[1].Trim();
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                return "No material asset at '" + materialPath + "'. assets-find with t:Material lists the ones this project has.";
            }

            List<Face> faces;
            List<int> indexes;
            error = TryResolveFaces(mesh, sections.Length > 2 ? sections[2] : string.Empty, out faces, out indexes);
            if (error != null) return error;

            var renderer = mesh.GetComponent<MeshRenderer>();
            var previous = new Dictionary<string, int>(StringComparer.Ordinal);
            var alreadyOn = 0;
            foreach (var face in faces)
            {
                var current = ProBuilderFaceMaterial(renderer, face);
                if (current == material) alreadyOn++;
                else CountName(previous, current == null ? "no material" : "'" + current.name + "'");
            }

            if (alreadyOn == faces.Count)
            {
                return "Already at value: all " + faces.Count.ToString(CultureInfo.InvariantCulture) + " face(s) already use '"
                    + material.name + "'. Nothing was changed.";
            }

            RecordUndo(mesh, "Conduit Set ProBuilder Face Material");
            mesh.SetMaterial(faces, material);
            RebuildProBuilderMesh(mesh);

            // Read back after the rebuild, which recomputes submeshes and can remap the renderer's slots.
            renderer = mesh.GetComponent<MeshRenderer>();
            var nowOn = 0;
            foreach (var face in faces)
            {
                if (ProBuilderFaceMaterial(renderer, face) == material) nowOn++;
            }

            var slots = renderer == null ? 0 : renderer.sharedMaterials.Length;
            return "Assigned '" + material.name + "' to " + faces.Count.ToString(CultureInfo.InvariantCulture) + " face(s): they were on "
                + DescribeCounts(previous) + (alreadyOn > 0 ? ", and " + alreadyOn.ToString(CultureInfo.InvariantCulture) + " already on '" + material.name + "'" : string.Empty)
                + ". Read back after the rebuild: " + nowOn.ToString(CultureInfo.InvariantCulture) + " of " + faces.Count.ToString(CultureInfo.InvariantCulture)
                + " now use it" + (nowOn == faces.Count ? string.Empty : " - the rest did NOT take the material")
                + ". The renderer now has " + slots.ToString(CultureInfo.InvariantCulture) + " material slot(s), one per submesh - each extra slot is an extra DRAW CALL, so keep the count deliberate.";
        }

        /// <summary>The material a face renders with: the renderer's slot for the face's submesh.</summary>
        /// <param name="renderer">The mesh's renderer, or null.</param>
        /// <param name="face">The face to look up.</param>
        /// <returns>The face's material, or null when it has none.</returns>
        private static Material ProBuilderFaceMaterial(MeshRenderer renderer, Face face)
        {
            if (renderer == null) return null;
            var materials = renderer.sharedMaterials;
            return face.submeshIndex >= 0 && face.submeshIndex < materials.Length ? materials[face.submeshIndex] : null;
        }

        /// <summary>Adds one to a name's count.</summary>
        /// <param name="counts">Counts by name.</param>
        /// <param name="name">Name to count.</param>
        private static void CountName(Dictionary<string, int> counts, string name)
        {
            counts.TryGetValue(name, out int count);
            counts[name] = count + 1;
        }

        /// <summary>Describes counts by name, as "'Brick' x3, group 2 x1".</summary>
        /// <param name="counts">Counts by name.</param>
        /// <returns>The description, or "nothing else" when empty.</returns>
        private static string DescribeCounts(Dictionary<string, int> counts)
        {
            if (counts.Count == 0) return "nothing else";
            var parts = new List<string>();
            foreach (var pair in counts) parts.Add(pair.Key + " x" + pair.Value.ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", parts);
        }

        /// <summary>Sets the smoothing group, which decides where a surface reads as curved.</summary>
        /// <param name="args">Target, group number, then an optional face selector.</param>
        /// <returns>What was set, or a validation error.</returns>
        private static string ProBuilderSmoothingSetCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-smoothing-set <target> | <group> [| faces]. Group 0 is no smoothing.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            int group;
            if (!int.TryParse(sections[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out group))
            {
                return "'" + sections[1].Trim() + "' is not a smoothing group number.";
            }

            List<Face> faces;
            List<int> indexes;
            error = TryResolveFaces(mesh, sections.Length > 2 ? sections[2] : string.Empty, out faces, out indexes);
            if (error != null) return error;

            var previous = new Dictionary<string, int>(StringComparer.Ordinal);
            var alreadyIn = 0;
            foreach (var face in faces)
            {
                if (face.smoothingGroup == group) alreadyIn++;
                else CountName(previous, "group " + face.smoothingGroup.ToString(CultureInfo.InvariantCulture));
            }

            if (alreadyIn == faces.Count)
            {
                return "Already at value: all " + faces.Count.ToString(CultureInfo.InvariantCulture) + " face(s) are already in smoothing group "
                    + group.ToString(CultureInfo.InvariantCulture) + ". Nothing was changed.";
            }

            RecordUndo(mesh, "Conduit Set ProBuilder Smoothing");
            foreach (var face in faces) face.smoothingGroup = group;
            RebuildProBuilderMesh(mesh);

            var nowIn = 0;
            foreach (var face in faces)
            {
                if (face.smoothingGroup == group) nowIn++;
            }

            return "Set smoothing group " + group.ToString(CultureInfo.InvariantCulture) + " on " + faces.Count.ToString(CultureInfo.InvariantCulture)
                + " face(s): they were in " + DescribeCounts(previous)
                + (alreadyIn > 0 ? ", and " + alreadyIn.ToString(CultureInfo.InvariantCulture) + " already in group " + group.ToString(CultureInfo.InvariantCulture) : string.Empty)
                + ". Read back after the rebuild: " + nowIn.ToString(CultureInfo.InvariantCulture) + " of " + faces.Count.ToString(CultureInfo.InvariantCulture)
                + " are in it. Faces sharing a NON-ZERO group are shaded as one smooth surface where they meet; group 0 leaves every edge hard.";
        }

        /// <summary>Reads a colour written as hex, a colour name, or three or four numbers.</summary>
        /// <param name="text">Text to read.</param>
        /// <param name="color">Receives the colour.</param>
        /// <param name="error">Receives why it could not be read.</param>
        /// <returns>True on success.</returns>
        private static bool TryParseColor(string text, out Color color, out string error)
        {
            color = Color.white;
            error = null;

            var wanted = (text ?? string.Empty).Trim();
            if (wanted.Length == 0)
            {
                error = "No colour was given.";
                return false;
            }

            var numbers = wanted.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length == 3 || numbers.Length == 4)
            {
                var channels = new float[4] { 0f, 0f, 0f, 1f };
                for (var index = 0; index < numbers.Length; index++)
                {
                    if (!float.TryParse(
                            numbers[index], NumberStyles.Float, CultureInfo.InvariantCulture,
                            out channels[index]))
                    {
                        error = "'" + numbers[index] + "' is not a number, so '" + wanted
                            + "' does not read as colour channels.";
                        return false;
                    }
                }

                color = new Color(channels[0], channels[1], channels[2], channels[3]);
                return true;
            }

            // Unity's parser wants the hash for hex but not for a name, so bare hex gets one rather than
            // being refused for a punctuation mark the caller had no reason to expect was required.
            var candidate = wanted;
            if (candidate[0] != '#' && (candidate.Length == 6 || candidate.Length == 8)
                && IsHexadecimal(candidate))
            {
                candidate = "#" + candidate;
            }

            if (ColorUtility.TryParseHtmlString(candidate, out color)) return true;

            error = "'" + wanted + "' is not a colour. Write it as #rrggbb or #rrggbbaa, a name such as "
                + "red, or channels from 0 to 1 as 'r g b' or 'r g b a'.";
            return false;
        }

        /// <summary>Whether every character is a hexadecimal digit.</summary>
        /// <param name="text">Text to test.</param>
        /// <returns>True when it is all hex digits.</returns>
        private static bool IsHexadecimal(string text)
        {
            foreach (var character in text)
            {
                var hex = (character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F');
                if (!hex) return false;
            }

            return true;
        }

        /// <summary>
        /// Quantises a colour to 8 bits per channel and packs it into one integer, giving the comparison
        /// ProBuilder's own Color32 matching makes but with an equality a hash set can use directly.
        /// </summary>
        /// <param name="color">Colour to pack.</param>
        /// <returns>The packed rgba value.</returns>
        private static uint PackColor32(Color color)
        {
            var quantised = (Color32)color;
            return ((uint)quantised.r << 24) | ((uint)quantised.g << 16)
                | ((uint)quantised.b << 8) | quantised.a;
        }

        /// <summary>Formats a colour for a confirmation line, as hex plus its alpha when not opaque.</summary>
        /// <param name="color">Colour to format.</param>
        /// <returns>A compact description.</returns>
        private static string FormatColor(Color color)
        {
            var opaque = color.a >= 1f;
            return "#" + ColorUtility.ToHtmlStringRGB(color)
                + (opaque
                    ? string.Empty
                    : " alpha " + color.a.ToString("0.###", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Writes a vertex colour onto whichever elements are named, which is ProBuilder's Vertex Colors
        /// window. Which elements are named changes the RESULT, not just the reach: colouring a face writes
        /// that face's own vertices and leaves a crisp boundary, where colouring a vertex also writes its
        /// coincident twins so the colour blends outward across every face sharing it - the same split
        /// ProBuilder makes between its Face and Vertex edit modes.
        /// </summary>
        /// <param name="args">Target, a colour, then an optional element selector.</param>
        /// <returns>What was coloured, or a validation error.</returns>
        private static string ProBuilderVertexColorSetCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-vertex-color-set <target> | <color> [| elements]. color is "
                    + "#rrggbb, #rrggbbaa, a name such as red, channels as 'r g b', or clear to remove the "
                    + "colours outright. elements is a face list, 'vertices:0,1' or 'edges:0-1', and "
                    + "defaults to the whole mesh.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            var wanted = sections[1].Trim();
            var selector = sections.Length > 2 ? sections[2].Trim() : PB_ALL_FACES;
            var clearing = string.Equals(wanted, "clear", StringComparison.OrdinalIgnoreCase);

            if (clearing) return ClearProBuilderVertexColors(mesh, selector);

            Color color;
            if (!TryParseColor(wanted, out color, out error)) return error;

            // ProBuilder's own palette converts to linear when the project is in linear colour space, so a
            // colour written straight through would render lighter than the same value applied through the
            // Vertex Colors window. Measured: the window applies col.linear, this matches it.
            var applied = PlayerSettings.colorSpace == ColorSpace.Linear ? color.linear : color;

            List<int> vertices;
            bool blended;
            error = TryResolveColorTargets(mesh, selector, out vertices, out blended);
            if (error != null) return error;

            // GetColors fills white for a mesh that carries no colour array, so this is always as long as
            // the positions - which the colors setter requires and throws over otherwise.
            var colors = mesh.GetColors();
            var target = PackColor32(applied);
            var alreadyColoured = 0;
            foreach (var index in vertices)
            {
                if (PackColor32(colors[index]) == target) alreadyColoured++;
            }

            if (alreadyColoured == vertices.Count)
            {
                return "Already at value: all " + vertices.Count.ToString(CultureInfo.InvariantCulture) + " named vertex position(s) are already "
                    + FormatColor(color) + ". Nothing was changed.";
            }

            foreach (var index in vertices) colors[index] = applied;

            RecordUndo(mesh, "Conduit Set ProBuilder Vertex Colors");
            mesh.colors = colors;
            RebuildProBuilderMesh(mesh);

            // Read back after the rebuild rather than trusting the array that was handed in.
            var written = mesh.GetColors();
            var nowColoured = 0;
            foreach (var index in vertices)
            {
                if (PackColor32(written[index]) == target) nowColoured++;
            }

            var evidence = " Before: " + alreadyColoured.ToString(CultureInfo.InvariantCulture) + " of them already had it; read back after the rebuild: "
                + nowColoured.ToString(CultureInfo.InvariantCulture) + " of " + vertices.Count.ToString(CultureInfo.InvariantCulture) + " have it"
                + (nowColoured == vertices.Count ? "." : " - the rest did NOT take the colour.");

            var renderer = mesh.GetComponent<MeshRenderer>();
            var material = renderer == null || renderer.sharedMaterial == null
                ? null : renderer.sharedMaterial;

            return "Set vertex colour " + FormatColor(color) + " on "
                + vertices.Count.ToString(CultureInfo.InvariantCulture) + " of "
                + mesh.vertexCount.ToString(CultureInfo.InvariantCulture) + " vertex position(s)"
                + (vertices.Count == mesh.vertexCount
                    ? " - every one of them, so the whole mesh is this colour."
                    : blended
                        ? ", including their coincident twins, so the colour blends outward across every "
                            + "face sharing them."
                        : " - the named faces' own vertices only, so the boundary stays crisp.")
                + evidence
                + (PlayerSettings.colorSpace == ColorSpace.Linear
                    ? " This project is in LINEAR colour space, so the value stored is the linear form, "
                        + "matching what ProBuilder's own palette writes."
                    : " This project is in GAMMA colour space, so the value is stored as given.")
                + " Only a material whose shader reads vertex colour shows them, and this object draws with "
                + (material == null ? "NO material" : "'" + material.name + "'")
                + " - ProBuilderDefault does read them. " + DescribeProBuilderMesh(mesh);
        }

        /// <summary>
        /// Removes a mesh's vertex colours outright. ProBuilder's own route is applying white, which stores
        /// a full array of white; this drops the array, which is what "no vertex colours" actually is.
        /// </summary>
        /// <param name="mesh">Mesh to clear.</param>
        /// <param name="selector">Element selector, which must address the whole mesh.</param>
        /// <returns>What was cleared, or a validation error.</returns>
        private static string ClearProBuilderVertexColors(ProBuilderMesh mesh, string selector)
        {
            // The colour array is all-or-nothing: it either matches the vertex count or does not exist, so
            // there is no such thing as clearing part of it. Applying white to a subset is the nearest real
            // operation and the caller is told to ask for that instead of being quietly given the lot.
            if (!string.Equals(selector, PB_ALL_FACES, StringComparison.OrdinalIgnoreCase))
            {
                return "clear removes the whole colour array, because a mesh either carries one colour per "
                    + "vertex or none at all - there is no partial state to leave behind. Pass 'all' to "
                    + "clear it, or set those elements to white (#ffffff), which is ProBuilder's own way of "
                    + "removing a colour from part of a mesh.";
            }

            if (mesh.colors == null)
            {
                return GetGameObjectPath(mesh.gameObject) + " already carries no vertex colours, so nothing "
                    + "was changed.";
            }

            RecordUndo(mesh, "Conduit Clear ProBuilder Vertex Colors");
            mesh.colors = null;
            RebuildProBuilderMesh(mesh);

            return "Cleared the vertex colours from " + GetGameObjectPath(mesh.gameObject)
                + ", so it now carries no colour array at all rather than one full of white. "
                + DescribeProBuilderMesh(mesh);
        }

        /// <summary>
        /// Resolves which vertex positions a colour write lands on, and reports whether the selector asked
        /// for the blended form. A face selector takes that face's own vertices; a vertex or edge selector
        /// also takes their coincident twins, which is what makes the colour spread past the element.
        /// </summary>
        /// <param name="mesh">Mesh the indexes address.</param>
        /// <param name="selector">Face list, "vertices:...", "edges:...", "all" or "selected".</param>
        /// <param name="vertices">Receives the vertex indexes to write.</param>
        /// <param name="blended">Receives whether coincident twins were included.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string TryResolveColorTargets(
            ProBuilderMesh mesh, string selector, out List<int> vertices, out bool blended)
        {
            vertices = new List<int>();
            blended = false;
            var wanted = (selector ?? string.Empty).Trim();
            if (wanted.Length == 0) wanted = PB_ALL_FACES;

            if (wanted.StartsWith("vertices:", StringComparison.OrdinalIgnoreCase)
                || wanted.StartsWith("edges:", StringComparison.OrdinalIgnoreCase))
            {
                var named = new List<int>();
                if (wanted.StartsWith("edges:", StringComparison.OrdinalIgnoreCase))
                {
                    List<Edge> edges;
                    var edgeError = TryResolveEdges(mesh, wanted.Substring("edges:".Length), out edges);
                    if (edgeError != null) return edgeError;

                    foreach (var edge in edges)
                    {
                        named.Add(edge.a);
                        named.Add(edge.b);
                    }
                }
                else
                {
                    var vertexError = TryResolveVertices(
                        mesh, wanted.Substring("vertices:".Length), out named);
                    if (vertexError != null) return vertexError;
                }

                blended = true;
                var seen = new HashSet<int>();
                foreach (var index in mesh.GetCoincidentVertices(named))
                {
                    if (seen.Add(index)) vertices.Add(index);
                }

                return vertices.Count > 0 ? null : "Those elements resolved to no vertices.";
            }

            List<Face> faces;
            List<int> faceIndexes;
            var error = TryResolveFaces(mesh, wanted, out faces, out faceIndexes);
            if (error != null) return error;

            var visited = new HashSet<int>();
            foreach (var face in faces)
            {
                foreach (var index in face.distinctIndexes)
                {
                    if (visited.Add(index)) vertices.Add(index);
                }
            }

            return vertices.Count > 0 ? null : "Those faces resolved to no vertices.";
        }

        #endregion

        #region Selection operations

        /// <summary>
        /// Resolves the mesh and the face set that nearly every operation verb begins with, so one shape of
        /// validation is shared rather than restated per verb.
        /// </summary>
        /// <param name="sections">Split command sections.</param>
        /// <param name="faceSection">Index of the optional face selector.</param>
        /// <param name="fallback">Selector used when the section is absent.</param>
        /// <param name="mesh">Receives the mesh.</param>
        /// <param name="faces">Receives the faces.</param>
        /// <param name="indexes">Receives the face indexes.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string TryResolveMeshAndFaces(
            string[] sections, int faceSection, string fallback,
            out ProBuilderMesh mesh, out List<Face> faces, out List<int> indexes)
        {
            faces = null;
            indexes = null;

            var error = TryFindProBuilderMesh(
                sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            var selector = sections.Length > faceSection ? sections[faceSection] : string.Empty;
            if (selector.Trim().Length == 0) selector = fallback;
            return TryResolveFaces(mesh, selector, out faces, out indexes);
        }

        /// <summary>Renders a face index list for a report.</summary>
        /// <param name="indexes">Indexes to render.</param>
        /// <returns>A comma separated list.</returns>
        private static string JoinIndexes(IEnumerable<int> indexes)
        {
            var builder = new StringBuilder();
            foreach (var index in indexes)
            {
                if (builder.Length > 0) builder.Append(", ");
                builder.Append(index.ToString(CultureInfo.InvariantCulture));
            }

            return builder.Length == 0 ? "(none)" : builder.ToString();
        }

        /// <summary>Applies a computed face set as the mesh selection and reports it.</summary>
        /// <param name="mesh">Mesh being selected on.</param>
        /// <param name="result">Faces to select.</param>
        /// <param name="what">What the operation was, for the report.</param>
        /// <param name="before">Face count selected before.</param>
        /// <returns>The report line.</returns>
        private static string ApplyFaceSelection(
            ProBuilderMesh mesh, ICollection<Face> result, string what, int before)
        {
            mesh.SetSelectedFaces(result);
            EditorUtility.SetDirty(mesh);

            var selected = new List<int>();
            foreach (var index in mesh.selectedFaceIndexes) selected.Add(index);

            return what + ": " + before.ToString(CultureInfo.InvariantCulture) + " -> "
                + selected.Count.ToString(CultureInfo.InvariantCulture) + " face(s) selected on "
                + GetGameObjectPath(mesh.gameObject) + ". Indexes: " + JoinIndexes(selected);
        }

        /// <summary>Widens a selection to the neighbouring faces, optionally only across shallow angles.</summary>
        /// <param name="args">Target, then an optional angle limit and face selector.</param>
        /// <returns>What is now selected, or a validation error.</returns>
        private static string ProBuilderSelectGrowCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 2, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            var angle = 180f;
            if (sections.Length > 1 && sections[1].Trim().Length > 0
                && !float.TryParse(sections[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out angle))
            {
                return "'" + sections[1].Trim() + "' is not an angle in degrees.";
            }

            var before = mesh.selectedFaceCount;
            var grown = ElementSelection.GrowSelection(mesh, faces, angle);
            return ApplyFaceSelection(mesh, new List<Face>(grown), "Grew the selection", before);
        }

        /// <summary>Selects the loop or ring running through the given faces.</summary>
        /// <param name="args">Target, loop or ring, then an optional face selector.</param>
        /// <returns>What is now selected, or a validation error.</returns>
        private static string ProBuilderSelectLoopCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-select-loop <target> | <kind> [| faces]. kind is loop or ring.";
            }

            var kind = sections[1].Trim().ToLowerInvariant();
            if (kind != "loop" && kind != "ring")
            {
                return "'" + kind + "' is not a kind. Use loop (along the faces) or ring (across them).";
            }

            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 2, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            var before = mesh.selectedFaceCount;
            var result = ElementSelection.GetFaceLoop(mesh, faces.ToArray(), kind == "ring");
            return ApplyFaceSelection(mesh, new List<Face>(result), "Selected the face " + kind, before);
        }

        /// <summary>
        /// Selects the edge loop or ring running through the named edges, which is ProBuilder's Select Edge
        /// Loop and Select Edge Ring. These are separate operations from the FACE loop and ring: a loop runs
        /// end to end along the edges, a ring crosses them, and both address edges rather than faces.
        /// </summary>
        /// <param name="args">Target, the kind, then an optional edge selector and reach.</param>
        /// <param name="ring">True for the ring, false for the loop.</param>
        /// <returns>What is now selected, or a validation error.</returns>
        private static string ProBuilderSelectEdgeLoopCommand(string args, bool ring)
        {
            var verb = ring ? "probuilder-select-edge-ring" : "probuilder-select-edge-loop";
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            List<Edge> edges;
            error = TryResolveEdges(mesh, sections.Length > 1 ? sections[1] : string.Empty, out edges);
            if (error != null) return error;

            // ProBuilder's own default is the WHOLE loop - its Iterative Selection preference defaults to
            // false - and iterative restricts the spread to the edges neighbouring the ones named, so a
            // caller steps outwards one band at a time instead of taking the lot.
            var reach = sections.Length > 2 ? sections[2].Trim().ToLowerInvariant() : string.Empty;
            if (reach.Length == 0) reach = "whole";
            if (reach != "whole" && reach != "iterative")
            {
                return "'" + reach + "' is not a reach. Use whole (the default, the entire loop) or "
                    + "iterative (only the edges neighbouring the ones named).";
            }

            var iterative = reach == "iterative";
            var before = mesh.selectedEdgeCount;
            object result;

            if (ring)
            {
                if (!TryInvokeProBuilderRuntimeStatic(
                        "UnityEngine.ProBuilder.MeshOperations.ElementSelection",
                        iterative ? "GetEdgeRingIterative" : "GetEdgeRing",
                        new[] { typeof(ProBuilderMesh), typeof(IEnumerable<Edge>) },
                        new object[] { mesh, edges }, out result, out error))
                {
                    return error;
                }

                var found = result as IEnumerable<Edge>;
                if (found == null) return "ProBuilder returned no edges for that ring.";

                return ApplyProBuilderEdgeSelection(mesh, new List<Edge>(found), verb, before, iterative);
            }

            // GetEdgeLoop hands the loop back through an OUT parameter and returns only whether it closed,
            // so the edges are read out of the argument array rather than the return value.
            var arguments = new object[] { mesh, edges, null };
            if (!TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.MeshOperations.ElementSelection",
                    iterative ? "GetEdgeLoopIterative" : "GetEdgeLoop",
                    new[]
                    {
                        typeof(ProBuilderMesh), typeof(IEnumerable<Edge>), typeof(Edge[]).MakeByRefType(),
                    },
                    arguments, out result, out error))
            {
                return error;
            }

            var loop = arguments[2] as Edge[];
            if (loop == null || loop.Length == 0)
            {
                return "ProBuilder found no loop through those "
                    + edges.Count.ToString(CultureInfo.InvariantCulture)
                    + " edge(s), so the selection was left as it was. A loop needs the edges to continue "
                    + "into neighbours with matching topology; a triangulated surface often has none.";
            }

            return ApplyProBuilderEdgeSelection(mesh, new List<Edge>(loop), verb, before, iterative);
        }

        /// <summary>
        /// Reads or sets ProBuilder's X-Ray view, which fades the selection handles that geometry is
        /// hiding. It is EDITOR-WIDE rather than per object, so this verb takes no target.
        /// </summary>
        /// <param name="args">An optional state.</param>
        /// <returns>What the state now is, or a validation error.</returns>
        private static string ProBuilderXRayCommand(string args)
        {
            var tokens = SplitCommandTokens(args ?? string.Empty, false);
            var requested = tokens.Length > 0 ? tokens[0].Trim().ToLowerInvariant() : string.Empty;

            var drawing = ProBuilderEditorType("UnityEditor.ProBuilder.EditorHandleDrawing");
            var property = drawing == null
                ? null
                : drawing.GetProperty(
                    "xRay",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
            if (property == null)
            {
                return "This ProBuilder build does not expose its X-Ray setting, so it cannot be read or "
                    + "changed here.";
            }

            var current = Convert.ToBoolean(property.GetValue(null, null));
            if (requested.Length == 0)
            {
                return "ProBuilder X-Ray is " + (current ? "ON" : "OFF")
                    + ". It fades the selection handles that geometry is hiding, so it changes what a SCENE"
                    + " screenshot shows only while ProBuilder is drawing handles - an element selection "
                    + "with its editor open. It does not change the mesh or how the object renders in a "
                    + "build. Pass on or off to change it.";
            }

            bool wanted;
            if (requested == "on") wanted = true;
            else if (requested == "off") wanted = false;
            else return "'" + requested + "' is not a state. Pass on or off, or nothing to read it.";

            if (current == wanted)
            {
                return "ProBuilder X-Ray was already " + (wanted ? "ON" : "OFF") + ", so nothing changed.";
            }

            property.SetValue(null, wanted, null);
            SceneView.RepaintAll();

            return "Set ProBuilder X-Ray " + (wanted ? "ON" : "OFF") + ", was "
                + (current ? "ON" : "OFF") + ". This is an editor-wide preference and it PERSISTS across "
                + "sessions, so it stays this way until something sets it back.";
        }

        /// <summary>
        /// Moves vertices to an EXACT position, which is ProBuilder's Vertex Position Editor. It writes the
        /// whole coincident group behind each vertex, because moving one raw vertex of a group and leaving
        /// its twins behind tears the surface open.
        /// </summary>
        /// <param name="args">Target, the position, then an optional vertex selector and space.</param>
        /// <returns>What moved, or a validation error.</returns>
        private static string ProBuilderVertSetPositionCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-vert-set-position <target> | <position> [| vertices] "
                    + "[| space:local|world]. position is three numbers. vertices defaults to the current "
                    + "selection. probuilder-vert-translate moves by an offset instead.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            Vector3 position;
            if (!TryParseVector3(sections[1].Trim(), out position))
            {
                return "'" + sections[1].Trim() + "' is not three numbers. Write the position as 'x y z'.";
            }

            List<int> vertices;
            error = TryResolveVertices(mesh, sections.Length > 2 ? sections[2] : string.Empty, out vertices);
            if (error != null) return error;

            var space = sections.Length > 3 ? sections[3].Trim().ToLowerInvariant() : string.Empty;
            if (space.Length == 0) space = "local";
            if (space != "local" && space != "world")
            {
                return "'" + space + "' is not a space. Pass local (the default, the mesh's own "
                    + "coordinates) or world.";
            }

            // SetSharedVertexPosition takes MODEL space, so a world position is brought into the object's
            // frame first - which is what the Vertex Position Editor's own World Space toggle does.
            var model = space == "world" ? mesh.transform.InverseTransformPoint(position) : position;

            // Raw indexes are what every other vertex verb takes and what a mesh read reports, so they are
            // what this accepts too; they are mapped to their shared groups here rather than making the
            // caller work in an index space nothing else uses.
            // The mesh's own lookup property is internal, so it is rebuilt here through the PUBLIC helper
            // that fills the same table - one pass over the shared vertices rather than a scan per vertex.
            var lookup = new Dictionary<int, int>();
            SharedVertex.GetSharedVertexLookup(mesh.sharedVertices, lookup);

            var groups = new HashSet<int>();
            foreach (var vertex in vertices)
            {
                int shared;
                if (!lookup.TryGetValue(vertex, out shared))
                {
                    return "Vertex " + vertex.ToString(CultureInfo.InvariantCulture) + " has no shared-vertex "
                        + "entry on this mesh, so its position cannot be set. probuilder-cleanup rebuilds "
                        + "that table.";
                }

                groups.Add(shared);
            }

            // Every named vertex would land on ONE point, so naming more than one group is a collapse
            // rather than a move - and there is already a verb for that which reports it as such.
            if (groups.Count > 1)
            {
                return "Those " + vertices.Count.ToString(CultureInfo.InvariantCulture)
                    + " vertex index(es) cover " + groups.Count.ToString(CultureInfo.InvariantCulture)
                    + " separate positions, and setting them all to one point would COLLAPSE them together "
                    + "rather than move them. Name one vertex (or one coincident group), or use "
                    + "probuilder-collapse-vertices, which does that deliberately and says so. Nothing was "
                    + "changed.";
            }

            var handle = 0;
            foreach (var shared in groups) handle = shared;
            var moved = mesh.positions[vertices[0]];
            if ((moved - model).sqrMagnitude < 1e-12f)
            {
                return "Already at value: vertex " + vertices[0].ToString(CultureInfo.InvariantCulture) + " is already at "
                    + FormatVector3(model) + " in LOCAL space. Nothing was changed.";
            }

            RecordUndo(mesh, "Conduit Set ProBuilder Vertex Position");
            mesh.SetSharedVertexPosition(handle, model);
            RebuildProBuilderMesh(mesh);

            // Read back after the rebuild rather than echoing the position that was asked for.
            var landed = mesh.positions[vertices[0]];
            return "Moved the coincident group behind vertex "
                + vertices[0].ToString(CultureInfo.InvariantCulture) + " from " + FormatVector3(moved)
                + " to " + FormatVector3(landed) + " in LOCAL space, read back after the rebuild"
                + ((landed - model).sqrMagnitude < 1e-8f ? string.Empty : " - NOT the " + FormatVector3(model) + " requested")
                + (space == "world"
                    ? ", converted from the world position given"
                    : string.Empty)
                + ". That is " + mesh.sharedVertices[handle].Count.ToString(CultureInfo.InvariantCulture)
                + " raw vertex position(s) written as one, so the surface stays closed. "
                + DescribeProBuilderMesh(mesh)
                + " Counts are unchanged - only a position moved, so indexes still hold. Collision does not "
                + "follow: run probuilder-colliders-rebuild.";
        }

        /// <summary>Applies an edge selection and reports what it changed.</summary>
        /// <param name="mesh">Mesh to select on.</param>
        /// <param name="edges">Edges to select.</param>
        /// <param name="verb">Verb name, for the undo label.</param>
        /// <param name="before">How many edges were selected beforehand.</param>
        /// <param name="iterative">Whether the restricted reach was used.</param>
        /// <returns>The confirmation line.</returns>
        private static string ApplyProBuilderEdgeSelection(
            ProBuilderMesh mesh, List<Edge> edges, string verb, int before, bool iterative)
        {
            RecordUndo(mesh, "Conduit " + verb);
            mesh.SetSelectedEdges(edges);

            return "Selected " + edges.Count.ToString(CultureInfo.InvariantCulture) + " edge(s), up from "
                + before.ToString(CultureInfo.InvariantCulture) + (iterative ? ", reaching only the "
                    + "neighbouring edges" : ", taking the whole run")
                + ". They are now the mesh's EDGE selection, so probuilder-bevel-edges, "
                + "probuilder-extrude-edges and probuilder-subdivide-edges act on them without naming "
                + "pairs. The geometry was not changed.";
        }

        /// <summary>Spreads a selection across every face reachable without crossing a sharp angle.</summary>
        /// <param name="args">Target, then an optional angle limit and face selector.</param>
        /// <returns>What is now selected, or a validation error.</returns>
        private static string ProBuilderSelectFloodCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 2, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            var angle = 30f;
            if (sections.Length > 1 && sections[1].Trim().Length > 0
                && !float.TryParse(sections[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out angle))
            {
                return "'" + sections[1].Trim() + "' is not an angle in degrees.";
            }

            var before = mesh.selectedFaceCount;
            var flooded = ElementSelection.FloodSelection(mesh, faces, angle);
            return ApplyFaceSelection(
                mesh, new List<Face>(flooded),
                "Flooded the selection across angles up to "
                    + angle.ToString("0.##", CultureInfo.InvariantCulture) + " degrees", before);
        }

        #endregion

        #region Topology operations

        /// <summary>
        /// Rounds edges off, which is what stops geometry reading as raw blocks - ProBuilder's Bevel Edges.
        /// </summary>
        /// <param name="args">Target, amount, then an optional edge selector.</param>
        /// <returns>What was bevelled, or a validation error.</returns>
        private static string ProBuilderBevelEdgesCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-bevel-edges <target> | <amount> [| edges]. edges is selected (the "
                    + "default), vertex-index pairs such as '0-1, 2-3', or 'faces:0,1' for every edge "
                    + "belonging to those faces.";
            }

            float amount;
            if (!float.TryParse(sections[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
            {
                return "'" + sections[1].Trim() + "' is not a bevel amount.";
            }

            if (amount <= 0f) return "The bevel amount must be greater than zero.";

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            List<Edge> edges;
            error = TryResolveEdges(mesh, sections.Length > 2 ? sections[2] : string.Empty, out edges);
            if (error != null) return error;

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit Bevel ProBuilder Edges");
            var created = Bevel.BevelEdges(mesh, edges, amount);
            RebuildProBuilderMesh(mesh);

            if (created == null || created.Count == 0)
            {
                return "Bevelled nothing: ProBuilder refused those " + edges.Count.ToString(CultureInfo.InvariantCulture)
                    + " edge(s), which usually means the amount is wider than the geometry can take. The mesh is unchanged at "
                    + DescribeProBuilderMesh(mesh) + ".";
            }

            return "Bevelled " + edges.Count.ToString(CultureInfo.InvariantCulture) + " edge(s) by "
                + amount.ToString("0.###", CultureInfo.InvariantCulture) + ", adding "
                + created.Count.ToString(CultureInfo.InvariantCulture) + " face(s). faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> " + mesh.faceCount.ToString(CultureInfo.InvariantCulture)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>Merges several faces into one, which is how an over-dense surface gets simplified.</summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>What was merged, or a validation error.</returns>
        private static string ProBuilderFaceMergeCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 1, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            if (faces.Count < 2) return "Merging needs at least two faces; " + faces.Count.ToString(CultureInfo.InvariantCulture) + " was given.";

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit Merge ProBuilder Faces");
            var merged = MergeElements.Merge(mesh, faces);
            RebuildProBuilderMesh(mesh);

            if (merged == null)
            {
                return "Merged nothing: ProBuilder returned no face. The mesh is unchanged at " + DescribeProBuilderMesh(mesh) + ".";
            }

            return "Merged " + indexes.Count.ToString(CultureInfo.InvariantCulture) + " face(s) into one. faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> " + mesh.faceCount.ToString(CultureInfo.InvariantCulture)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>
        /// Splits faces onto a NEW GameObject the way ProBuilder's own Detach and Duplicate actions do:
        /// the whole object is instantiated, the copy deletes every face that was not named, and the
        /// original deletes the named ones only when they are being moved rather than copied.
        /// <para>
        /// Children are removed from the copy and the original's children re-synchronised, because an
        /// instantiated ProBuilder hierarchy otherwise carries duplicated child meshes sharing one asset.
        /// </para>
        /// </summary>
        /// <param name="mesh">Mesh the faces belong to.</param>
        /// <param name="indexes">Face indexes moving or copying to the new object.</param>
        /// <param name="deleteFromSource">True to remove them from the original, which is a detach.</param>
        /// <param name="copy">Receives the new GameObject.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string SplitFacesToNewObject(
            ProBuilderMesh mesh, List<int> indexes, bool deleteFromSource, out GameObject copy)
        {
            copy = null;

            // A set rather than a list scan: a dense mesh makes the per-face Contains quadratic, and this
            // runs over every face on the object.
            var named = new HashSet<int>(indexes);
            var inverse = new List<int>();
            for (var face = 0; face < mesh.faceCount; face++)
            {
                if (!named.Contains(face)) inverse.Add(face);
            }

            if (deleteFromSource && inverse.Count == 0)
            {
                return "Those are ALL of this mesh's faces, so moving them to a new object would leave the "
                    + "original empty. ProBuilder refuses the same case; gameobject-duplicate is what "
                    + "copies a whole object.";
            }

            var instance = UnityEngine.Object.Instantiate(mesh.gameObject, mesh.transform.parent);
            var copyMesh = instance.GetComponent<ProBuilderMesh>();
            if (copyMesh == null)
            {
                UnityEngine.Object.DestroyImmediate(instance);
                return "The copy came back without a ProBuilderMesh, so nothing was changed.";
            }

            // Only a detach makes the copy's mesh asset its own; a duplicate deliberately leaves the two
            // sharing until the copy is reduced, exactly as ProBuilder's two actions differ.
            if (deleteFromSource) copyMesh.MakeUnique();
            UnityEditor.ProBuilder.EditorUtility.SynchronizeWithMeshFilter(copyMesh);

            for (var child = instance.transform.childCount - 1; child >= 0; child--)
            {
                UnityEngine.Object.DestroyImmediate(instance.transform.GetChild(child).gameObject);
            }

            foreach (var child in mesh.transform.GetComponentsInChildren<ProBuilderMesh>())
            {
                UnityEditor.ProBuilder.EditorUtility.SynchronizeWithMeshFilter(child);
            }

            copyMesh.DeleteFaces(inverse);
            RebuildProBuilderMesh(copyMesh);

            if (deleteFromSource)
            {
                mesh.DeleteFaces(indexes);
                RebuildProBuilderMesh(mesh);
            }

            instance.name = GameObjectUtility.GetUniqueNameForSibling(mesh.transform.parent, mesh.gameObject.name);
            Undo.RegisterCreatedObjectUndo(instance, "Conduit Split ProBuilder Faces");
            copy = instance;
            return null;
        }

        /// <summary>Breaks faces away from the mesh so they can be moved as their own surface.</summary>
        /// <param name="args">Target, then an optional destination and face selector.</param>
        /// <returns>What was detached, or a validation error.</returns>
        private static string ProBuilderFaceDetachCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 2, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            bool toObject;
            error = TryParseSplitDestination(sections.Length > 1 ? sections[1] : string.Empty, out toObject);
            if (error != null) return error;

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit Detach ProBuilder Faces");

            if (toObject)
            {
                GameObject copy;
                error = SplitFacesToNewObject(mesh, indexes, true, out copy);
                if (error != null) return error;

                return "Detached " + indexes.Count.ToString(CultureInfo.InvariantCulture)
                    + " face(s) to a NEW object: " + GetGameObjectPath(copy) + " ["
                    + copy.GetInstanceID().ToString(CultureInfo.InvariantCulture) + "]. It holds only those "
                    + "faces; the original kept the rest and is now " + DescribeProBuilderMesh(mesh)
                    + ". Face indexes shifted on BOTH meshes; re-read them with probuilder-mesh-get.";
            }

            var detached = ExtrudeElements.DetachFaces(mesh, faces, true);
            RebuildProBuilderMesh(mesh);

            return "Detached " + indexes.Count.ToString(CultureInfo.InvariantCulture)
                + " face(s) into a SUBMESH of the same object, producing "
                + (detached == null ? 0 : detached.Count).ToString(CultureInfo.InvariantCulture)
                + " new face(s) and removing the originals. faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>
        /// Reads the destination that the detach and duplicate verbs share, defaulting to a new object
        /// because that is what ProBuilder's own Detach Faces and Duplicate Faces actions default to.
        /// </summary>
        /// <param name="text">The destination section, which may be empty.</param>
        /// <param name="toObject">True for a new GameObject, false for a submesh of the same object.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string TryParseSplitDestination(string text, out bool toObject)
        {
            toObject = true;

            var wanted = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (wanted.Length == 0 || wanted == "gameobject") return null;
            if (wanted == "submesh")
            {
                toObject = false;
                return null;
            }

            return "'" + (text ?? string.Empty).Trim() + "' is not a destination. Use gameobject (the "
                + "default, a separate object) or submesh (kept inside this object).";
        }

        /// <summary>Copies faces to a new object or a submesh, leaving the originals where they are.</summary>
        /// <param name="args">Target, then an optional destination and face selector.</param>
        /// <returns>What was duplicated, or a validation error.</returns>
        private static string ProBuilderFaceDuplicateCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 2, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            bool toObject;
            error = TryParseSplitDestination(sections.Length > 1 ? sections[1] : string.Empty, out toObject);
            if (error != null) return error;

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit Duplicate ProBuilder Faces");

            if (toObject)
            {
                GameObject copy;
                error = SplitFacesToNewObject(mesh, indexes, false, out copy);
                if (error != null) return error;

                return "Copied " + indexes.Count.ToString(CultureInfo.InvariantCulture)
                    + " face(s) to a NEW object: " + GetGameObjectPath(copy) + " ["
                    + copy.GetInstanceID().ToString(CultureInfo.InvariantCulture) + "], which holds only "
                    + "those faces. The original is untouched at " + DescribeProBuilderMesh(mesh) + ".";
            }

            var duplicated = ExtrudeElements.DetachFaces(mesh, faces, false);
            RebuildProBuilderMesh(mesh);

            return "Copied " + indexes.Count.ToString(CultureInfo.InvariantCulture)
                + " face(s) into a SUBMESH of the same object, producing "
                + (duplicated == null ? 0 : duplicated.Count).ToString(CultureInfo.InvariantCulture)
                + " new face(s) and keeping the originals. faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>
        /// Copies faces in place with the copies' normals reversed, which is what gives a one-sided
        /// surface a back. Separate from duplicating because it produces no new object and no submesh.
        /// </summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>What was added, or a validation error.</returns>
        private static string ProBuilderFaceDuplicateFlipCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 1, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit Duplicate And Flip ProBuilder Faces");
            AppendElements.DuplicateAndFlip(mesh, faces.ToArray());
            RebuildProBuilderMesh(mesh);

            return "Added a REVERSED copy of " + indexes.Count.ToString(CultureInfo.InvariantCulture)
                + " face(s) in place, which is what makes a one-sided wall or banner visible from behind. "
                + "faces " + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>Reverses face winding, the fix for geometry that renders inside out.</summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>What was flipped, or a validation error.</returns>
        private static string ProBuilderNormalsFlipCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 1, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            RecordUndo(mesh, "Conduit Flip ProBuilder Normals");
            foreach (var face in faces) face.Reverse();
            RebuildProBuilderMesh(mesh);

            return "Flipped " + indexes.Count.ToString(CultureInfo.InvariantCulture)
                + " face(s). Counts are unchanged at " + DescribeProBuilderMesh(mesh)
                + "; what changed is which side renders, so confirm it with a screenshot rather than a count.";
        }

        /// <summary>Makes neighbouring faces agree on a facing direction.</summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>What was conformed, or a validation error.</returns>
        private static string ProBuilderNormalsConformCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 1, PB_ALL_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            RecordUndo(mesh, "Conduit Conform ProBuilder Normals");
            var result = SurfaceTopology.ConformNormals(mesh, faces);
            RebuildProBuilderMesh(mesh);

            return "Conformed " + indexes.Count.ToString(CultureInfo.InvariantCulture)
                + " face(s) to the direction most of them already faced. ProBuilder reported: " + Convert.ToString(result)
                + ". Counts are unchanged at " + DescribeProBuilderMesh(mesh) + ".";
        }

        /// <summary>Recomputes normals and tangents, which is what fixes shading after heavy editing.</summary>
        /// <param name="args">Target only.</param>
        /// <returns>What was recalculated, or a validation error.</returns>
        private static string ProBuilderNormalsRecalculateCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            RecordUndo(mesh, "Conduit Recalculate ProBuilder Normals");
            Normals.CalculateNormals(mesh);
            Normals.CalculateTangents(mesh);
            RebuildProBuilderMesh(mesh);

            return "Recalculated normals and tangents on " + GetGameObjectPath(mesh.gameObject) + ". Counts are unchanged at "
                + DescribeProBuilderMesh(mesh) + "; this changes SHADING, so judge it from a screenshot. Smoothing groups "
                + "decide which edges come out soft - see probuilder-smoothing-set.";
        }

        /// <summary>Converts triangles back into quads, which is what keeps a mesh workable.</summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>What was converted, or a validation error.</returns>
        private static string ProBuilderToQuadsCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 1, PB_ALL_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit ProBuilder To Quads");
            var result = QuadUtility.ToQuads(mesh, faces, true);
            RebuildProBuilderMesh(mesh);

            return "Converted " + indexes.Count.ToString(CultureInfo.InvariantCulture) + " face(s) toward quads, giving "
                + (result == null ? 0 : result.Count).ToString(CultureInfo.InvariantCulture) + " face(s) back. faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> " + mesh.faceCount.ToString(CultureInfo.InvariantCulture)
                + ". Triangles that have no quad partner are left as triangles rather than forced.";
        }

        /// <summary>Splits faces into triangles, which some operations and exporters require.</summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>What was converted, or a validation error.</returns>
        private static string ProBuilderToTrianglesCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 1, PB_ALL_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit ProBuilder To Triangles");
            var result = SurfaceTopology.ToTriangles(mesh, faces);
            RebuildProBuilderMesh(mesh);

            return "Split " + indexes.Count.ToString(CultureInfo.InvariantCulture) + " face(s) into "
                + (result == null ? 0 : result.Length).ToString(CultureInfo.InvariantCulture) + " triangle face(s). faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> " + mesh.faceCount.ToString(CultureInfo.InvariantCulture)
                + ". This is not reversible by re-running probuilder-to-quads, which can only pair triangles that still match.";
        }

        #endregion

        #region Vertex, edge and transform operations

        /// <summary>
        /// Resolves a vertex selector, which addresses positions rather than faces.
        /// </summary>
        /// <param name="mesh">Mesh the indexes address.</param>
        /// <param name="selector">"all", "selected", or an index list.</param>
        /// <param name="vertices">Receives the vertex indexes.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string TryResolveVertices(ProBuilderMesh mesh, string selector, out List<int> vertices)
        {
            vertices = new List<int>();
            var wanted = (selector ?? string.Empty).Trim();
            if (wanted.Length == 0) wanted = PB_SELECTED_FACES;

            if (string.Equals(wanted, PB_ALL_FACES, StringComparison.OrdinalIgnoreCase))
            {
                for (var index = 0; index < mesh.vertexCount; index++) vertices.Add(index);
                return vertices.Count > 0 ? null : "This mesh has no vertices.";
            }

            if (string.Equals(wanted, PB_SELECTED_FACES, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var index in mesh.selectedVertices) vertices.Add(index);
                if (vertices.Count > 0) return null;

                // A face selection implies its vertices, which is what a person would expect after
                // selecting faces and then asking for a vertex operation.
                foreach (var face in mesh.GetSelectedFaces()) vertices.AddRange(face.distinctIndexes);
                return vertices.Count > 0
                    ? null
                    : "Nothing is selected on this mesh. Select faces with probuilder-select, or pass "
                        + PB_ALL_FACES + " or an explicit index list.";
            }

            foreach (var token in wanted.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int index;
                if (!int.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                {
                    return "'" + token.Trim() + "' is not a vertex index.";
                }

                if (index < 0 || index >= mesh.vertexCount)
                {
                    return "Vertex index " + index.ToString(CultureInfo.InvariantCulture) + " is outside this mesh, which has "
                        + mesh.vertexCount.ToString(CultureInfo.InvariantCulture) + " vertices numbered 0 to "
                        + (mesh.vertexCount - 1).ToString(CultureInfo.InvariantCulture) + ".";
                }

                vertices.Add(index);
            }

            return vertices.Count > 0 ? null : "No vertices were named.";
        }

        /// <summary>Parses one edge written as two vertex indexes.</summary>
        /// <param name="text">Text such as "12,13".</param>
        /// <param name="mesh">Mesh the indexes address.</param>
        /// <param name="edge">Receives the edge.</param>
        /// <param name="error">Receives why it could not be read.</param>
        /// <returns>True on success.</returns>
        private static bool TryParseEdge(string text, ProBuilderMesh mesh, out Edge edge, out string error)
        {
            edge = default(Edge);
            error = null;

            var parts = (text ?? string.Empty).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                error = "An edge is two vertex indexes, e.g. 12,13 - '" + (text ?? string.Empty).Trim() + "' is not.";
                return false;
            }

            int a, b;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out a)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
            {
                error = "'" + (text ?? string.Empty).Trim() + "' does not read as two vertex indexes.";
                return false;
            }

            if (a < 0 || a >= mesh.vertexCount || b < 0 || b >= mesh.vertexCount)
            {
                error = "An edge index is outside this mesh, which has "
                    + mesh.vertexCount.ToString(CultureInfo.InvariantCulture) + " vertices.";
                return false;
            }

            edge = new Edge(a, b);
            return true;
        }

        /// <summary>Collapses vertices that sit within a radius of each other, closing seams.</summary>
        /// <param name="args">Target, radius, then an optional vertex selector.</param>
        /// <returns>What was welded, or a validation error.</returns>
        private static string ProBuilderVertexWeldCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2) return "Usage: probuilder-vert-weld <target> | <radius> [| vertices].";

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            float radius;
            if (!float.TryParse(sections[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out radius))
            {
                return "'" + sections[1].Trim() + "' is not a radius.";
            }

            if (radius <= 0f) return "The weld radius must be greater than zero.";

            List<int> vertices;
            error = TryResolveVertices(mesh, sections.Length > 2 ? sections[2] : PB_ALL_FACES, out vertices);
            if (error != null) return error;

            var beforeVerts = mesh.vertexCount;
            RecordUndo(mesh, "Conduit Weld ProBuilder Vertices");
            var welded = VertexEditing.WeldVertices(mesh, vertices, radius);
            RebuildProBuilderMesh(mesh);

            if (mesh.vertexCount == beforeVerts)
            {
                return "Welded nothing: no two of those " + vertices.Count.ToString(CultureInfo.InvariantCulture)
                    + " vertices were within " + radius.ToString("0.###", CultureInfo.InvariantCulture)
                    + ". The mesh is unchanged at " + DescribeProBuilderMesh(mesh) + ".";
            }

            return "Welded within " + radius.ToString("0.###", CultureInfo.InvariantCulture) + ": verts "
                + beforeVerts.ToString(CultureInfo.InvariantCulture) + " -> " + mesh.vertexCount.ToString(CultureInfo.InvariantCulture)
                + " (" + (welded == null ? 0 : welded.Length).ToString(CultureInfo.InvariantCulture) + " reported), "
                + DescribeProBuilderMesh(mesh) + ".";
        }

        /// <summary>Separates vertices that share a position, which is how a seam is opened.</summary>
        /// <param name="args">Target, then an optional vertex selector.</param>
        /// <returns>What was split, or a validation error.</returns>
        private static string ProBuilderVertexSplitCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            List<int> vertices;
            error = TryResolveVertices(mesh, sections.Length > 1 ? sections[1] : PB_SELECTED_FACES, out vertices);
            if (error != null) return error;

            var beforeVerts = mesh.vertexCount;
            RecordUndo(mesh, "Conduit Split ProBuilder Vertices");
            VertexEditing.SplitVertices(mesh, vertices);
            RebuildProBuilderMesh(mesh);

            return "Split " + vertices.Count.ToString(CultureInfo.InvariantCulture) + " vertex handle(s): verts "
                + beforeVerts.ToString(CultureInfo.InvariantCulture) + " -> " + mesh.vertexCount.ToString(CultureInfo.InvariantCulture)
                + ", " + DescribeProBuilderMesh(mesh) + ". Splitting is what lets two faces move apart at a shared corner.";
        }

        /// <summary>Moves vertices by an offset, which is how a shape is pushed out of its generated form.</summary>
        /// <param name="args">Target, offset, then an optional vertex selector.</param>
        /// <returns>What was moved, or a validation error.</returns>
        private static string ProBuilderVertexTranslateCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2) return "Usage: probuilder-vert-translate <target> | <x y z> [| vertices].";

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            Vector3 offset;
            if (!TryParseVector3(sections[1].Trim(), out offset))
            {
                return "The offset must be three numbers, e.g. 0 1 0.";
            }

            List<int> vertices;
            error = TryResolveVertices(mesh, sections.Length > 2 ? sections[2] : PB_SELECTED_FACES, out vertices);
            if (error != null) return error;

            RecordUndo(mesh, "Conduit Translate ProBuilder Vertices");
            VertexPositioning.TranslateVertices(mesh, vertices, offset);
            RebuildProBuilderMesh(mesh);

            return "Moved " + vertices.Count.ToString(CultureInfo.InvariantCulture) + " vertex handle(s) by "
                + offset.ToString() + " in the mesh's LOCAL space. Counts are unchanged at " + DescribeProBuilderMesh(mesh)
                + "; the shape moved, so judge it from a screenshot.";
        }

        /// <summary>Builds a face bridging two edges, which is how two open shapes get joined.</summary>
        /// <param name="args">Target and two edges.</param>
        /// <returns>What was bridged, or a validation error.</returns>
        private static string ProBuilderEdgeBridgeCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 3) return "Usage: probuilder-edge-bridge <target> | <edgeA> | <edgeB>. Each edge is two vertex indexes, e.g. 12,13.";

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            Edge a, b;
            string edgeError;
            if (!TryParseEdge(sections[1], mesh, out a, out edgeError)) return edgeError;
            if (!TryParseEdge(sections[2], mesh, out b, out edgeError)) return edgeError;

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit Bridge ProBuilder Edges");
            var created = AppendElements.Bridge(mesh, a, b, true);
            RebuildProBuilderMesh(mesh);

            if (created == null)
            {
                return "Bridged nothing: ProBuilder refused those two edges. They must both be OPEN - an edge "
                    + "with a face on each side has nothing to bridge to. The mesh is unchanged at " + DescribeProBuilderMesh(mesh) + ".";
            }

            return "Bridged the two edges with a new face. faces " + beforeFaces.ToString(CultureInfo.InvariantCulture)
                + " -> " + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + ", " + DescribeProBuilderMesh(mesh) + ".";
        }

        /// <summary>Inserts edge loops through faces, adding the divisions an edit needs.</summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>What was connected, or a validation error.</returns>
        private static string ProBuilderEdgeConnectCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 1, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit Connect ProBuilder Faces");
            var created = ConnectElements.Connect(mesh, faces);
            RebuildProBuilderMesh(mesh);

            return "Connected " + indexes.Count.ToString(CultureInfo.InvariantCulture) + " face(s), giving "
                + (created == null ? 0 : created.Length).ToString(CultureInfo.InvariantCulture) + " face(s) back. faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> " + mesh.faceCount.ToString(CultureInfo.InvariantCulture)
                + ". This is the same operation probuilder-subdivide performs over a whole shape.";
        }

        /// <summary>Moves the object's pivot, which decides what it rotates and scales around.</summary>
        /// <param name="args">Target, then center or a world position.</param>
        /// <returns>Where the pivot went, or a validation error.</returns>
        private static string ProBuilderPivotSetCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2) return "Usage: probuilder-set-pivot <target> | <where>. where is center, selection, or three numbers as a world position.";

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            var where = sections[1].Trim();
            var before = mesh.transform.position;

            RecordUndo(mesh, "Conduit Set ProBuilder Pivot");
            RecordUndo(mesh.transform, "Conduit Set ProBuilder Pivot");

            Vector3 worldPosition;
            if (string.Equals(where, "center", StringComparison.OrdinalIgnoreCase))
            {
                MeshTransform.CenterPivot(mesh, null);
            }
            else if (string.Equals(where, "selection", StringComparison.OrdinalIgnoreCase))
            {
                var selected = new List<int>();
                foreach (var face in mesh.GetSelectedFaces()) selected.AddRange(face.distinctIndexes);
                if (selected.Count == 0) return "Nothing is selected, so there is no selection to centre the pivot on.";
                MeshTransform.CenterPivot(mesh, selected.ToArray());
            }
            else if (TryParseVector3(where, out worldPosition))
            {
                MeshTransform.SetPivot(mesh, worldPosition);
            }
            else
            {
                return "'" + where + "' is not a pivot target. Use center, selection, or three numbers as a world position.";
            }

            RebuildProBuilderMesh(mesh);

            return "Pivot moved from " + before.ToString() + " to " + mesh.transform.position.ToString()
                + ". The geometry did NOT move - the vertices were rewritten to compensate, so the shape looks identical and only rotation and scaling behave differently.";
        }

        /// <summary>Bakes the transform's scale into the mesh, which is what stops scaled geometry breaking lighting.</summary>
        /// <param name="args">Target only.</param>
        /// <returns>What was frozen, or a validation error.</returns>
        private static string ProBuilderFreezeTransformCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            var before = mesh.transform.localScale;
            RecordUndo(mesh, "Conduit Freeze ProBuilder Transform");
            RecordUndo(mesh.transform, "Conduit Freeze ProBuilder Transform");
            MeshTransform.FreezeScaleTransform(mesh);
            RebuildProBuilderMesh(mesh);

            return "Froze the scale into the mesh: localScale " + before.ToString() + " -> "
                + mesh.transform.localScale.ToString() + ". The shape is unchanged on screen. A NON-UNIFORM scale on a "
                + "renderer skews lighting and breaks batching, so freezing it is what makes scaled geometry behave.";
        }

        #endregion

        #region Asset and editor operations

        /// <summary>
        /// Invokes one static entry point in ProBuilder's EDITOR assembly, which keeps several genuinely
        /// useful actions behind internal access.
        /// </summary>
        /// <remarks>
        /// The overload is pinned by exact parameter types rather than by name: several of these ship more
        /// than one overload, and a name-only lookup throws AmbiguousMatchException before the call is ever
        /// attempted. A missing type or method is reported BY NAME so an SDK change is attributable.
        /// </remarks>
        /// <param name="typeName">Full type name inside the editor assembly.</param>
        /// <param name="methodName">Static method to call.</param>
        /// <param name="parameterTypes">Exact parameter types, including the leading mesh.</param>
        /// <param name="arguments">Arguments to pass.</param>
        /// <param name="result">Receives the return value.</param>
        /// <param name="error">Receives why the call could not be made.</param>
        /// <returns>True when the method ran.</returns>
        private static bool TryInvokeProBuilderEditor(
            string typeName, string methodName, Type[] parameterTypes, object[] arguments,
            out object result, out string error)
        {
            result = null;
            error = null;

            System.Reflection.Assembly editorAssembly = null;
            foreach (var candidate in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (candidate.GetName().Name == "Unity.ProBuilder.Editor") { editorAssembly = candidate; break; }
            }

            if (editorAssembly == null)
            {
                error = "ProBuilder's editor assembly is not loaded, so this action is unavailable.";
                return false;
            }

            var type = editorAssembly.GetType(typeName);
            if (type == null)
            {
                error = "This ProBuilder build has no " + typeName + ", so this action is unavailable.";
                return false;
            }

            var method = type.GetMethod(
                methodName,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic,
                null, parameterTypes, null);
            if (method == null)
            {
                error = "This ProBuilder build has no " + typeName + "." + methodName
                    + " with that signature, so this action is unavailable.";
                return false;
            }

            try { result = method.Invoke(null, arguments); return true; }
            catch (Exception invokeError) { error = "ProBuilder refused the call: " + Unwrap(invokeError); return false; }
        }

        /// <summary>Rebuilds the mesh for shipping and optionally generates its lightmap UVs.</summary>
        /// <param name="args">Target, then an optional lightmap flag.</param>
        /// <returns>What was optimized, or a validation error.</returns>
        private static string ProBuilderOptimizeCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            var generateLightmapUVs = true;
            if (sections.Length > 1 && sections[1].Trim().Length > 0)
            {
                var flag = sections[1].Trim().ToLowerInvariant();
                if (flag != "lightmap" && flag != "nolightmap")
                {
                    return "'" + flag + "' is not a mode. Use lightmap (default) or nolightmap.";
                }

                generateLightmapUVs = flag == "lightmap";
            }

            RecordUndo(mesh, "Conduit Optimize ProBuilder Mesh");

            // The rebuild runs FIRST and Optimize last. ToMesh rebuilds the rendered mesh from the authoring
            // data, which discards the UV2 Optimize just generated - measured: with the rebuild afterwards
            // the mesh reported 0 UV2 entries while the call claimed to have made them.
            RebuildProBuilderMesh(mesh);
            UnityEditor.ProBuilder.EditorMeshUtility.Optimize(mesh, generateLightmapUVs);

            var filter = mesh.GetComponent<MeshFilter>();
            var uv2Count = filter == null || filter.sharedMesh == null ? 0 : filter.sharedMesh.uv2.Length;

            // The outcome is read back from the mesh rather than assumed from the request, so a generation
            // that produced nothing is reported as that instead of as success.
            string outcome;
            if (!generateLightmapUVs)
            {
                outcome = "Lightmap UVs were NOT requested, so this mesh cannot take a baked lightmap until they are generated.";
            }
            else if (uv2Count > 0)
            {
                outcome = "Lightmap UVs generated: the mesh now carries " + uv2Count.ToString(CultureInfo.InvariantCulture)
                    + " UV2 entries. Geometry with no UV2 is skipped by a bake even when it is marked static, so this "
                    + "and the static flags are BOTH needed before it lights.";
            }
            else
            {
                outcome = "Lightmap UVs were requested but the mesh still reports NO UV2 entries, so a bake would "
                    + "skip it. ProBuilder generates them from the mesh's own unwrap parameters; check those rather "
                    + "than treating this call as having succeeded.";
            }

            return "Optimized " + GetGameObjectPath(mesh.gameObject) + " (" + DescribeProBuilderMesh(mesh) + "). " + outcome;
        }

        /// <summary>Rebuilds the colliders to match edited geometry, which they do not follow on their own.</summary>
        /// <param name="args">Target only.</param>
        /// <returns>What was rebuilt, or a validation error.</returns>
        private static string ProBuilderCollidersRebuildCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            var colliders = mesh.GetComponents<Collider>();
            if (colliders.Length == 0)
            {
                return "This object has no collider, so there was nothing to rebuild. Add one with "
                    + "gameobject-component-add before a player can stand on this geometry.";
            }

            RecordUndo(mesh, "Conduit Rebuild ProBuilder Colliders");
            UnityEditor.ProBuilder.EditorMeshUtility.RebuildColliders(mesh);

            return "Rebuilt " + colliders.Length.ToString(CultureInfo.InvariantCulture) + " collider(s) on "
                + GetGameObjectPath(mesh.gameObject) + " to match " + DescribeProBuilderMesh(mesh)
                + ". A collider does NOT follow edited geometry on its own, so a shape that was extruded or "
                + "subdivided keeps its old collision until this runs.";
        }

        /// <summary>Removes degenerate triangles and unused vertices, which accumulate through editing.</summary>
        /// <param name="args">Target only.</param>
        /// <returns>What was removed, or a validation error.</returns>
        private static string ProBuilderCleanupCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            var beforeFaces = mesh.faceCount;
            var beforeVerts = mesh.vertexCount;

            // MeshValidation carries these now; the DeleteElements pair is obsolete. The replacements FILL a
            // list and return whether anything went, rather than returning the removed indexes, so the counts
            // come off the lists.
            var degenerate = new List<int>();
            var unused = new List<int>();

            RecordUndo(mesh, "Conduit Clean ProBuilder Mesh");
            MeshValidation.RemoveDegenerateTriangles(mesh, degenerate);
            MeshValidation.RemoveUnusedVertices(mesh, unused);
            RebuildProBuilderMesh(mesh);

            if (degenerate.Count == 0 && unused.Count == 0)
            {
                return "Nothing needed removing: no degenerate triangles and no unused vertices. The mesh is "
                    + "unchanged at " + DescribeProBuilderMesh(mesh) + ".";
            }

            return "Removed " + degenerate.Count.ToString(CultureInfo.InvariantCulture)
                + " degenerate triangle(s) and " + unused.Count.ToString(CultureInfo.InvariantCulture)
                + " unused vertex(es). faces " + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + ", verts "
                + beforeVerts.ToString(CultureInfo.InvariantCulture) + " -> " + mesh.vertexCount.ToString(CultureInfo.InvariantCulture)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>Reports mesh faults without changing anything, so a shape can be judged before shipping.</summary>
        /// <param name="args">Target only.</param>
        /// <returns>The findings, or a validation error.</returns>
        private static string ProBuilderValidateCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            var degenerate = MeshValidation.ContainsDegenerateTriangles(mesh);

            var openEdges = 0;
            foreach (var face in mesh.faces)
            {
                if (MeshValidation.ContainsNonContiguousTriangles(mesh, face)) openEdges++;
            }

            var filter = mesh.GetComponent<MeshFilter>();
            var uv2Count = filter == null || filter.sharedMesh == null ? 0 : filter.sharedMesh.uv2.Length;
            var flags = GameObjectUtility.GetStaticEditorFlags(mesh.gameObject);
            var contributesGI = (flags & StaticEditorFlags.ContributeGI) != 0;
            var renderer = mesh.GetComponent<MeshRenderer>();
            var materialSlots = renderer == null ? 0 : renderer.sharedMaterials.Length;
            var nullMaterial = renderer != null && renderer.sharedMaterial == null;

            var report = new StringBuilder();
            report.Append(GetGameObjectPath(mesh.gameObject)).Append("  ").Append(DescribeProBuilderMesh(mesh));
            report.Append(Environment.NewLine).Append("degenerate triangles : ")
                .Append(degenerate ? "YES - run probuilder-cleanup" : "none");
            report.Append(Environment.NewLine).Append("non-contiguous faces : ")
                .Append(openEdges == 0
                    ? "none"
                    : openEdges.ToString(CultureInfo.InvariantCulture) + " - these usually render "
                        + "unpredictably, but a face with a hole cut through it reports here legitimately, "
                        + "so check whether this shape was built with holes before treating it as a fault");
            report.Append(Environment.NewLine).Append("material slots       : ")
                .Append(materialSlots.ToString(CultureInfo.InvariantCulture))
                .Append(nullMaterial ? " - the first is NULL, so this renders magenta" : " (one draw call each)");
            report.Append(Environment.NewLine).Append("lightmap UVs (UV2)   : ")
                .Append(uv2Count == 0 ? "NONE - run probuilder-lightmap-uvs before baking" : uv2Count.ToString(CultureInfo.InvariantCulture));
            report.Append(Environment.NewLine).Append("contributes GI       : ")
                .Append(contributesGI ? "yes" : "NO - it will be skipped by a bake");
            report.Append(Environment.NewLine).Append("Nothing was changed by this call.");

            return report.ToString();
        }

        /// <summary>
        /// Scales geometry so its bounding box matches an exact size, which is how a shape gets real-world
        /// dimensions without putting a non-uniform scale on the transform.
        /// </summary>
        /// <param name="args">Target, then the wanted size as three numbers.</param>
        /// <returns>The measured size before and after, or a validation error.</returns>
        private static string ProBuilderFitToSizeCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-fit-to-size <target> | <size>. size is three numbers, e.g. "
                    + "'4 3 0.2' for a wall.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            Vector3 wanted;
            if (!TryParseVector3(sections[1].Trim(), out wanted))
            {
                return "'" + sections[1].Trim() + "' is not three numbers. Write the size as 'x y z'.";
            }

            var filter = mesh.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                return GetGameObjectPath(mesh.gameObject) + " has no built mesh to measure, so there is "
                    + "nothing to fit.";
            }

            var before = filter.sharedMesh.bounds.size;

            // A Shape-tool object regenerates from the size held on its ProBuilderShape, so vertices
            // written here would be discarded the next time it rebuilds - its own size is set instead.
            // That component is INTERNAL to Unity.ProBuilder even though its members read as public, so
            // it is only reachable by reflection: naming the type here does not compile.
            var shapeType = typeof(ProBuilderMesh).Assembly
                .GetType("UnityEngine.ProBuilder.Shapes.ProBuilderShape");
            var shape = shapeType == null ? null : mesh.GetComponent(shapeType);
            if (shape != null)
            {
                var sizeProperty = shapeType.GetProperty("size");
                if (sizeProperty == null)
                {
                    return GetGameObjectPath(mesh.gameObject) + " is a Shape-tool object, but this "
                        + "ProBuilder build does not expose ProBuilderShape.size, so its authored size "
                        + "cannot be set. Nothing was changed.";
                }

                var authoredBefore = (Vector3)sizeProperty.GetValue(shape, null);
                RecordUndo(shape, "Conduit Fit ProBuilder Shape To Size");
                RecordUndo(mesh, "Conduit Fit ProBuilder Shape To Size");
                sizeProperty.SetValue(shape, wanted, null);

                try
                {
                    InvokeInstanceMethod(shape, "UpdateComponent", null);
                }
                catch (Exception rebuildError)
                {
                    // The authored size is already written, so the component is reported as it now stands
                    // rather than as though the call had changed nothing.
                    var thrown = rebuildError.InnerException ?? rebuildError;
                    return "Set the authored size on " + GetGameObjectPath(mesh.gameObject) + " to "
                        + FormatVector3((Vector3)sizeProperty.GetValue(shape, null))
                        + ", but regenerating the shape from it threw: " + thrown.Message;
                }

                RebuildProBuilderMesh(mesh);

                // Measured rather than assumed: UpdateComponent is internal too, so a ProBuilder build
                // that renamed it would otherwise be reported as a resize that never actually happened.
                var shapeResult = filter.sharedMesh.bounds.size;
                var authored = (Vector3)sizeProperty.GetValue(shape, null);
                if ((shapeResult - before).sqrMagnitude < Mathf.Epsilon
                    && (wanted - before).sqrMagnitude > Mathf.Epsilon)
                {
                    return "Set the authored size on " + GetGameObjectPath(mesh.gameObject) + " to "
                        + FormatVector3(authored) + ", but the mesh still measures "
                        + FormatVector3(shapeResult) + ", so the shape did NOT regenerate. This ProBuilder "
                        + "build does not expose ProBuilderShape.UpdateComponent under that name.";
                }

                return "Fitted " + GetGameObjectPath(mesh.gameObject)
                    + ", a Shape-tool object, so its authored size was set and the shape regenerated from "
                    + "it. Measured " + FormatVector3(before) + " -> " + FormatVector3(shapeResult)
                    + ", requested " + FormatVector3(wanted) + ", authored size "
                    + FormatVector3(authoredBefore) + " -> " + FormatVector3(authored) + ". " + DescribeProBuilderMesh(mesh)
                    + " Collision does not follow geometry: run probuilder-colliders-rebuild.";
            }

            RecordUndo(mesh, "Conduit Fit ProBuilder Mesh To Size");
            UnityEngine.ProBuilder.MeshUtility.FitToSize(mesh, filter.sharedMesh.bounds, wanted);
            RebuildProBuilderMesh(mesh);

            return "Fitted " + GetGameObjectPath(mesh.gameObject) + ": measured " + FormatVector3(before)
                + " -> " + FormatVector3(filter.sharedMesh.bounds.size) + ", requested "
                + FormatVector3(wanted) + ". " + DescribeProBuilderMesh(mesh)
                + " The GEOMETRY moved and the transform scale was left alone, so this does not distort "
                + "children or colliders the way a non-uniform transform scale would. Collision does not "
                + "follow: run probuilder-colliders-rebuild.";
        }

        /// <summary>
        /// Turns the ContributeGI static flag on or off, which is the difference between geometry that
        /// bakes and geometry a bake silently skips.
        /// </summary>
        /// <param name="args">Target, then an optional state.</param>
        /// <returns>What the flag was and what it is now, or a validation error.</returns>
        private static string ProBuilderLightmapStaticCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            var target = sections.Length > 0 ? sections[0].Trim() : string.Empty;
            if (target.Length == 0)
            {
                return "Usage: probuilder-lightmap-static <target> [| state]. state is on or off, and "
                    + "defaults to on.";
            }

            GameObject gameObject;
            var error = TryFindGameObject(target, out gameObject);
            if (error != null) return error;

            var requested = sections.Length > 1 ? sections[1].Trim().ToLowerInvariant() : "on";
            bool wanted;
            if (requested.Length == 0 || requested == "on") wanted = true;
            else if (requested == "off") wanted = false;
            else return "'" + sections[1].Trim() + "' is not a state. Pass on or off.";

            var flags = GameObjectUtility.GetStaticEditorFlags(gameObject);
            var had = (flags & StaticEditorFlags.ContributeGI) != 0;
            if (had == wanted)
            {
                return GetGameObjectPath(gameObject) + " already has ContributeGI "
                    + (wanted ? "on" : "off") + ", so nothing was changed.";
            }

            RecordUndo(gameObject, "Conduit Set ProBuilder Lightmap Static");
            GameObjectUtility.SetStaticEditorFlags(
                gameObject,
                wanted ? flags | StaticEditorFlags.ContributeGI : flags & ~StaticEditorFlags.ContributeGI);
            MarkGameObjectSceneDirty(gameObject);

            var filter = gameObject.GetComponent<MeshFilter>();
            var uv2Count = filter == null || filter.sharedMesh == null ? 0 : filter.sharedMesh.uv2.Length;

            return "ContributeGI on " + GetGameObjectPath(gameObject) + ": " + (had ? "on" : "off")
                + " -> " + (wanted ? "on" : "off")
                + (wanted
                    ? ". It will now be included in a lightmap bake"
                        + (uv2Count == 0
                            ? ", but it still has NO lightmap UVs - run probuilder-lightmap-uvs or a bake "
                                + "produces nothing for it."
                            : ", and it already carries " + uv2Count.ToString(CultureInfo.InvariantCulture)
                                + " lightmap UVs.")
                    : ". A bake will now skip it entirely and it will be lit by light probes instead.");
        }

        /// <summary>Merges several ProBuilder objects into one, which is how a draw-call count comes down.</summary>
        /// <param name="args">Target that receives the result, then the others.</param>
        /// <returns>What was combined, or a validation error.</returns>
        private static string ProBuilderCombineCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2) return "Usage: probuilder-merge-objects <target> | <others>. others is a comma separated list of targets merged INTO the first.";

            ProBuilderMesh target;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out target);
            if (error != null) return error;

            var meshes = new List<ProBuilderMesh> { target };
            var names = new List<string>();
            foreach (var token in sections[1].Split(','))
            {
                var trimmed = token.Trim();
                if (trimmed.Length == 0) continue;

                ProBuilderMesh other;
                error = TryFindProBuilderMesh(trimmed, out other);
                if (error != null) return error;
                if (other == target) continue;

                meshes.Add(other);
                names.Add(GetGameObjectPath(other.gameObject));
            }

            if (meshes.Count < 2) return "Combining needs at least one other mesh besides the target.";

            var beforeFaces = target.faceCount;
            RecordUndo(target, "Conduit Combine ProBuilder Meshes");
            var result = CombineMeshes.Combine(meshes, target);
            RebuildProBuilderMesh(target);

            return "Combined " + names.Count.ToString(CultureInfo.InvariantCulture) + " mesh(es) into "
                + GetGameObjectPath(target.gameObject) + ": " + string.Join(", ", names.ToArray())
                + ". faces " + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + target.faceCount.ToString(CultureInfo.InvariantCulture) + ", " + DescribeProBuilderMesh(target)
                + ". ProBuilder returned " + (result == null ? 0 : result.Count).ToString(CultureInfo.InvariantCulture)
                + " object(s), splitting the result when it would exceed a mesh's vertex limit. The SOURCE objects "
                + "still exist - remove them with gameobject-destroy once the result looks right.";
        }

        /// <summary>Mirrors an object across an axis, which is how symmetrical geometry gets built once.</summary>
        /// <param name="args">Target, axes, then an optional duplicate flag.</param>
        /// <returns>What was mirrored, or a validation error.</returns>
        private static string ProBuilderMirrorCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2) return "Usage: probuilder-mirror-objects <target> | <axes> [| mode]. axes is three numbers such as -1 1 1; mode is duplicate (default) or inplace.";

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            Vector3 scale;
            if (!TryParseVector3(sections[1].Trim(), out scale))
            {
                return "The axes must be three numbers, e.g. -1 1 1 to mirror across X.";
            }

            var duplicate = true;
            if (sections.Length > 2 && sections[2].Trim().Length > 0)
            {
                var flag = sections[2].Trim().ToLowerInvariant();
                if (flag != "duplicate" && flag != "inplace") return "'" + flag + "' is not a mode. Use duplicate (default) or inplace.";
                duplicate = flag == "duplicate";
            }

            object result;
            if (!TryInvokeProBuilderEditor(
                "UnityEditor.ProBuilder.Actions.MirrorObjects", "Mirror",
                new[] { typeof(ProBuilderMesh), typeof(Vector3), typeof(bool) },
                new object[] { mesh, scale, duplicate }, out result, out error))
            {
                return error;
            }

            var mirrored = result as ProBuilderMesh;
            if (mirrored == null) return "Mirrored nothing: ProBuilder returned no mesh.";

            return "Mirrored " + GetGameObjectPath(mesh.gameObject) + " across " + scale.ToString()
                + (duplicate ? " into a NEW object: " + GetGameObjectPath(mirrored.gameObject) : " in place")
                + " (" + DescribeProBuilderMesh(mirrored) + "). A negative axis reverses winding, which ProBuilder "
                + "corrects as part of the mirror - check with a screenshot rather than assuming.";
        }

        /// <summary>Writes the mesh out as a project asset so it can be reused or handed to another tool.</summary>
        /// <param name="args">Target, then an optional kind.</param>
        /// <returns>Where it was written, or a validation error.</returns>
        private static string ProBuilderExportCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            var kind = sections.Length > 1 && sections[1].Trim().Length > 0
                ? sections[1].Trim().ToLowerInvariant() : "mesh";

            // The interchange formats write a FILE and so need a destination, where mesh and prefab write
            // a project asset whose path ProBuilder derives. ProBuilder's own export goes through a save
            // dialog, which would block the editor thread and time out this call, so the exporters are
            // driven directly and the bytes are written here instead.
            if (kind == "obj" || kind == "ply" || kind == "stl-ascii" || kind == "stl-binary")
            {
                var destination = sections.Length > 2 ? sections[2].Trim() : string.Empty;
                if (destination.Length == 0)
                {
                    return "Exporting as " + kind + " writes a file, so it needs a path: "
                        + "probuilder-export <target> | " + kind + " | <path>.";
                }

                var full = System.IO.Path.GetFullPath(destination);
                var folder = System.IO.Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
                {
                    return "There is no folder at '" + folder + "', so nothing was written. Create it first, "
                        + "or give a path inside one that exists.";
                }

                // Unlike obj and ply, the STL exporter writes the file itself rather than handing back its
                // contents, so it takes the path and there is nothing to write afterwards.
                if (kind == "stl-ascii" || kind == "stl-binary")
                {
                    return ExportProBuilderStl(mesh, kind == "stl-binary", full);
                }

                return ExportProBuilderInterchange(mesh, kind, full, folder);
            }

            if (kind != "mesh" && kind != "prefab")
            {
                return "'" + kind + "' is not a kind. Use mesh (the default) or prefab for a project asset, "
                    + "or obj, ply, stl-ascii or stl-binary with a path for an interchange file.";
            }

            object result;
            if (!TryInvokeProBuilderEditor(
                "UnityEditor.ProBuilder.Actions.ExportAsset", kind == "prefab" ? "ExportPrefab" : "ExportMesh",
                new[] { typeof(ProBuilderMesh), typeof(bool) },
                new object[] { mesh, false }, out result, out error))
            {
                return error;
            }

            var path = Convert.ToString(result);
            if (string.IsNullOrEmpty(path)) return "ProBuilder wrote no asset and reported no path.";

            AssetDatabase.Refresh();
            return "Exported " + GetGameObjectPath(mesh.gameObject) + " as a " + kind + " asset: " + path
                + ". The scene object is unchanged and still ProBuilder-editable.";
        }

        /// <summary>
        /// Writes one mesh out as an interchange file through ProBuilder's own exporters, which hand back
        /// the file CONTENTS rather than writing them, so no save dialog is involved.
        /// </summary>
        /// <param name="mesh">Mesh to export.</param>
        /// <param name="kind">"obj" or "ply".</param>
        /// <param name="full">Absolute destination path, already resolved and checked.</param>
        /// <param name="folder">The folder that path sits in, where a sidecar goes.</param>
        /// <returns>What was written, or a user-facing error.</returns>
        private static string ExportProBuilderInterchange(
            ProBuilderMesh mesh, string kind, string full, string folder)
        {
            // The option types are named in the exporter signatures, so a missing one would be passed to
            // GetMethod as a null entry and throw before the call is ever attempted.
            var plyOptions = ProBuilderEditorType("UnityEditor.ProBuilder.PlyOptions");
            var objOptions = ProBuilderEditorType("UnityEditor.ProBuilder.ObjOptions");
            var modelEnumerable = ProBuilderEditorEnumerableType("UnityEditor.ProBuilder.Model");
            if (plyOptions == null || objOptions == null || modelEnumerable == null)
            {
                return "This ProBuilder build does not expose its exporter option types, so obj and ply "
                    + "export are unavailable here. probuilder-export with mesh or prefab still works.";
            }

            var name = mesh.gameObject.name;
            object exported;
            string error;

            if (kind == "ply")
            {
                var arguments = new object[] { new[] { mesh }, null, null };
                if (!TryInvokeProBuilderEditor(
                        "UnityEditor.ProBuilder.PlyExporter", "Export",
                        new[]
                        {
                            typeof(IEnumerable<ProBuilderMesh>), typeof(string).MakeByRefType(), plyOptions,
                        },
                        arguments, out exported, out error))
                {
                    return error;
                }

                if (!(exported is bool) || !(bool)exported)
                {
                    return "ProBuilder's PLY exporter refused this mesh, so nothing was written.";
                }

                System.IO.File.WriteAllText(full, Convert.ToString(arguments[1]));
                return FinishProBuilderExport(mesh, "PLY", full, null);
            }

            // A typed array, because the parameter is IEnumerable<Model> and an object[] would not satisfy
            // it. The model type resolved above, so this cannot be null here.
            var modelType = ProBuilderEditorType("UnityEditor.ProBuilder.Model");
            var modelArray = Array.CreateInstance(modelType, 1);
            modelArray.SetValue(Activator.CreateInstance(modelType, new object[] { name, mesh, true }), 0);

            var models = new object[] { name, modelArray, null, null, null, null };
            if (!TryInvokeProBuilderEditor(
                    "UnityEditor.ProBuilder.ObjExporter", "Export",
                    new[]
                    {
                        typeof(string), modelEnumerable,
                        typeof(string).MakeByRefType(), typeof(string).MakeByRefType(),
                        typeof(List<string>).MakeByRefType(), objOptions,
                    },
                    models, out exported, out error))
            {
                return error;
            }

            if (!(exported is bool) || !(bool)exported)
            {
                return "ProBuilder's OBJ exporter refused this mesh, so nothing was written.";
            }

            System.IO.File.WriteAllText(full, Convert.ToString(models[2]));

            // The library is named from the MESH, because that is the name the exporter wrote into the
            // obj's own "mtllib ./<name>.mtl" line - deriving it from the caller's filename instead leaves
            // a reference nothing resolves on any case-sensitive filesystem.
            var materialLibrary = Convert.ToString(models[3]);
            string materialPath = null;
            if (!string.IsNullOrEmpty(materialLibrary))
            {
                materialPath = System.IO.Path.Combine(folder, name + ".mtl");
                System.IO.File.WriteAllText(materialPath, materialLibrary);
            }

            return FinishProBuilderExport(mesh, "OBJ", full, materialPath);
        }

        /// <summary>Reports an interchange export and re-imports it when it landed inside the project.</summary>
        /// <param name="mesh">Mesh that was exported.</param>
        /// <param name="format">Display name of the format.</param>
        /// <param name="path">Absolute path written.</param>
        /// <param name="sidecar">Companion file written beside it, or null.</param>
        /// <returns>The confirmation line.</returns>
        private static string FinishProBuilderExport(
            ProBuilderMesh mesh, string format, string path, string sidecar)
        {
            var project = System.IO.Path.GetFullPath(".").Replace('\\', '/');
            var written = path.Replace('\\', '/');
            var insideProject = written.StartsWith(project, StringComparison.OrdinalIgnoreCase);
            if (insideProject) AssetDatabase.Refresh();

            return "Exported " + GetGameObjectPath(mesh.gameObject) + " as " + format + ": " + written
                + (sidecar != null
                    ? " with its material library at " + sidecar.Replace('\\', '/')
                        + ", which is the name the obj's own mtllib line points at"
                    : string.Empty)
                + ". " + DescribeProBuilderMesh(mesh)
                + (insideProject
                    ? " It is inside the project, so it was imported."
                    : " It is OUTSIDE the project, so Unity has not imported it.")
                + " The scene object is unchanged and still ProBuilder-editable.";
        }

        /// <summary>Resolves a type inside ProBuilder's editor assembly, or null when it is absent.</summary>
        /// <param name="typeName">Full type name.</param>
        /// <returns>The type, or null.</returns>
        private static Type ProBuilderEditorType(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "Unity.ProBuilder.Editor") continue;

                return assembly.GetType(typeName);
            }

            return null;
        }

        /// <summary>
        /// Resolves a type by full name across every loaded assembly, which the STL exporter needs because
        /// it ships in its own assembly rather than ProBuilder's runtime or editor one.
        /// </summary>
        /// <param name="typeName">Full type name.</param>
        /// <returns>The first match, or null when nothing loaded declares it.</returns>
        private static Type LoadedTypeByName(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var found = assembly.GetType(typeName);
                if (found != null) return found;
            }

            return null;
        }

        /// <summary>
        /// Writes one mesh out as STL through ProBuilder's own exporter, which takes a path and writes the
        /// file itself rather than returning contents.
        /// </summary>
        /// <param name="mesh">Mesh to export.</param>
        /// <param name="binary">True for the binary form, false for ASCII.</param>
        /// <param name="full">Absolute destination path.</param>
        /// <returns>What was written, or a user-facing error.</returns>
        private static string ExportProBuilderStl(ProBuilderMesh mesh, bool binary, string full)
        {
            var exporter = LoadedTypeByName("UnityEngine.ProBuilder.Stl.pb_Stl_Exporter");
            var fileType = LoadedTypeByName("UnityEngine.ProBuilder.Stl.FileType");
            if (exporter == null || fileType == null)
            {
                return "This ProBuilder build does not ship its STL exporter, so stl export is unavailable "
                    + "here. obj and ply still work.";
            }

            var method = exporter.GetMethod(
                "Export",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(GameObject[]), fileType }, null);
            if (method == null)
            {
                return "ProBuilder's STL exporter has no Export taking a path, objects and a file type in "
                    + "this build, so stl export is unavailable here.";
            }

            object wrote;
            try
            {
                wrote = method.Invoke(
                    null,
                    new object[]
                    {
                        full, new[] { mesh.gameObject }, Enum.ToObject(fileType, binary ? 1 : 0),
                    });
            }
            catch (Exception exportError)
            {
                return "ProBuilder's STL exporter threw: " + Unwrap(exportError) + ". Nothing was written.";
            }

            if (!(wrote is bool) || !(bool)wrote)
            {
                return "ProBuilder's STL exporter refused this mesh, so nothing was written.";
            }

            return FinishProBuilderExport(mesh, binary ? "STL (binary)" : "STL (ascii)", full, null);
        }

        /// <summary>The IEnumerable of an editor-assembly type, which an exporter signature asks for.</summary>
        /// <param name="typeName">Full element type name.</param>
        /// <returns>The constructed enumerable type, or null when the element type is absent.</returns>
        private static Type ProBuilderEditorEnumerableType(string typeName)
        {
            var element = ProBuilderEditorType(typeName);
            return element == null ? null : typeof(IEnumerable<>).MakeGenericType(element);
        }

        /// <summary>Removes the ProBuilder components, leaving plain renderable geometry behind.</summary>
        /// <param name="args">Target only.</param>
        /// <returns>What was stripped, or a validation error.</returns>
        private static string ProBuilderStripCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            var path = GetGameObjectPath(mesh.gameObject);
            var counts = DescribeProBuilderMesh(mesh);

            object ignored;
            if (!TryInvokeProBuilderEditor(
                "UnityEditor.ProBuilder.Actions.StripProBuilderScripts", "DoStrip",
                new[] { typeof(ProBuilderMesh), typeof(bool) },
                new object[] { mesh, true }, out ignored, out error))
            {
                return error;
            }

            return "Stripped the ProBuilder components from " + path + ", leaving its MeshFilter and "
                + "MeshRenderer with the geometry it had (" + counts + "). This is IRREVERSIBLE as an edit - "
                + "the shape still renders and still builds, but no probuilder verb can reach it again.";
        }

        #endregion

        #region Dispatch

        /// <summary>
        /// Routes the ProBuilder verbs. Returns null when the verb belongs to another area.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs taking their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        /// <summary>
        /// Turns geometry into a collision volume, which is ProBuilder's Set Collider and Set Trigger.
        /// <para>
        /// ProBuilder's own actions also attach a ColliderBehaviour or TriggerBehaviour, whose only job is
        /// to switch the Renderer off on entering play mode. That component is plain C# and is stripped
        /// from a VRChat upload, so it is NOT added here - it would be dead in the client while making the
        /// object look handled. Everything that survives the upload - the collider, isTrigger, convex, and
        /// the material - is applied, and the reply says the visibility is the caller's to deal with.
        /// </para>
        /// </summary>
        /// <param name="args">Target only.</param>
        /// <param name="asTrigger">True for the trigger form, which also forces the collider convex.</param>
        /// <returns>What was applied, or a validation error.</returns>
        private static string SetProBuilderCollisionVolume(string args, bool asTrigger)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(
                sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            RecordUndo(mesh.gameObject, asTrigger ? "Conduit Set ProBuilder Trigger" : "Conduit Set ProBuilder Collider");

            var collider = mesh.GetComponent<Collider>();
            var addedCollider = collider == null;
            if (addedCollider) collider = Undo.AddComponent<MeshCollider>(mesh.gameObject);

            var renderer = mesh.GetComponent<Renderer>();
            var addedRenderer = renderer == null;
            if (addedRenderer) renderer = Undo.AddComponent<MeshRenderer>(mesh.gameObject);

            // A MeshCollider only raises trigger events while it is CONVEX; a concave one is accepted and
            // then silently never fires. ProBuilder sets this for a trigger and leaves it alone otherwise.
            var meshCollider = collider as MeshCollider;
            var forcedConvex = false;
            if (asTrigger && meshCollider != null && !meshCollider.convex)
            {
                meshCollider.convex = true;
                forcedConvex = true;
            }

            collider.isTrigger = asTrigger;

            // The two visualisation materials are internal to Unity.ProBuilder, so they are read by
            // reflection; failing to get one costs the tint and nothing else, so it is reported rather
            // than aborting work that already landed.
            object found;
            string materialError;
            var applied = "left as it was";
            if (TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.BuiltinMaterials",
                    asTrigger ? "get_triggerMaterial" : "get_colliderMaterial",
                    Type.EmptyTypes, new object[0], out found, out materialError))
            {
                var material = found as Material;
                if (material != null && renderer != null)
                {
                    renderer.sharedMaterial = material;
                    applied = "set to ProBuilder's " + material.name;
                }
            }

            EditorUtility.SetDirty(mesh.gameObject);
            MarkGameObjectSceneDirty(mesh.gameObject);

            var existing = mesh.GetComponents<MonoBehaviour>();
            var entityPresent = false;
            foreach (var behaviour in existing)
            {
                if (behaviour == null) continue;

                var name = behaviour.GetType().Name;
                if (name == "ColliderBehaviour" || name == "TriggerBehaviour" || name == "Entity") entityPresent = true;
            }

            return (asTrigger ? "Set " : "Set ") + GetGameObjectPath(mesh.gameObject) + " as a "
                + (asTrigger ? "TRIGGER" : "COLLIDER") + " volume: "
                + (addedCollider ? "added a MeshCollider" : "kept its existing " + collider.GetType().Name)
                + ", isTrigger=" + (asTrigger ? "true" : "false")
                + (forcedConvex ? ", convex forced TRUE because a concave MeshCollider never raises trigger events" : string.Empty)
                + (addedRenderer ? ", added a MeshRenderer" : string.Empty)
                + ", material " + applied + "."
                + (asTrigger && meshCollider == null
                    ? " NOTE: its collider is not a MeshCollider, so convex does not apply - check the shape is one Unity can trigger from."
                    : string.Empty)
                // Convex is left alone for a solid collider, matching ProBuilder - but one still set from
                // an earlier trigger pass makes collision follow the HULL rather than the shape, and
                // nothing else reports that.
                + (!asTrigger && meshCollider != null && meshCollider.convex
                    ? " WARNING: its MeshCollider is still CONVEX, so solid collision follows the convex"
                        + " hull rather than the geometry - a player walks on the hull of any concave"
                        + " shape. Convex is left as found here, as ProBuilder leaves it; clear it with"
                        + " gameobject-component-modify when the shape is not convex."
                    : string.Empty)
                + " ProBuilder's own action also attaches a behaviour that hides the Renderer in play mode;"
                + " that component is plain C# and is stripped from a VRChat upload, so it is not added -"
                + " switch the Renderer off yourself, or give the object a real material, before shipping."
                + (entityPresent
                    ? " One of ProBuilder's entity behaviours is already on this object from its own toolbar; it was left alone and will still hide the Renderer in the EDITOR."
                    : string.Empty);
        }

        /// <summary>
        /// Splits the UVs of the named vertices into their own island, which is the UV editor's Split UVs.
        /// </summary>
        /// <param name="args">Target, then an optional vertex selector.</param>
        /// <returns>What was split, or a validation error.</returns>
        private static string ProBuilderUvSplitCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<int> vertices;
            var error = TryResolveMeshAndUvVertices(sections, 1, out mesh, out vertices);
            if (error != null) return error;

            object result;
            RecordUndo(mesh, "Conduit Split ProBuilder UVs");
            if (!TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.MeshOperations.UVEditing", "SplitUVs",
                    new[] { typeof(ProBuilderMesh), typeof(IEnumerable<int>) },
                    new object[] { mesh, vertices }, out result, out error))
            {
                return error;
            }

            var splitFaces = MarkUvFacesManual(mesh, vertices);
            RebuildProBuilderMesh(mesh);
            return "Split the UVs of " + vertices.Count.ToString(CultureInfo.InvariantCulture)
                + " vertex position(s) into their own island, so moving them no longer drags the "
                + "neighbouring faces' UVs with them. The "
                + splitFaces.ToString(CultureInfo.InvariantCulture)
                + " face(s) involved are now on MANUAL UVs, without which the rebuild would have "
                + "regenerated them and discarded this. " + DescribeProBuilderMesh(mesh)
                + " Positions are unchanged - this edits UVs only.";
        }

        /// <summary>
        /// Collapses the named vertices' UVs onto one point, which is the UV editor's Collapse UVs.
        /// </summary>
        /// <param name="args">Target, then an optional vertex selector.</param>
        /// <returns>What was collapsed, or a validation error.</returns>
        private static string ProBuilderUvCollapseCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<int> vertices;
            var error = TryResolveMeshAndUvVertices(sections, 1, out mesh, out vertices);
            if (error != null) return error;

            object result;
            RecordUndo(mesh, "Conduit Collapse ProBuilder UVs");
            if (!TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.MeshOperations.UVEditing", "CollapseUVs",
                    new[] { typeof(ProBuilderMesh), typeof(int[]) },
                    new object[] { mesh, vertices.ToArray() }, out result, out error))
            {
                return error;
            }

            var collapsedFaces = MarkUvFacesManual(mesh, vertices);
            RebuildProBuilderMesh(mesh);
            return "Collapsed " + vertices.Count.ToString(CultureInfo.InvariantCulture)
                + " vertex position(s) onto ONE UV coordinate, across "
                + collapsedFaces.ToString(CultureInfo.InvariantCulture)
                + " face(s) now on MANUAL UVs so the rebuild cannot regenerate them. "
                + DescribeProBuilderMesh(mesh)
                + " Positions are unchanged - this edits UVs only.";
        }

        /// <summary>
        /// Sews the named vertices' UVs together where they are within a distance of each other, which is
        /// the UV editor's Sew UVs - the seam-closing counterpart to a split.
        /// </summary>
        /// <param name="args">Target, a distance, then an optional vertex selector.</param>
        /// <returns>What was sewn, or a validation error.</returns>
        private static string ProBuilderUvSewCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-uv-sew <target> | <distance> [| vertices]. distance is how close "
                    + "two UVs must be in UV SPACE to be joined, so it is a fraction of the 0-1 square "
                    + "rather than a world measurement.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            float distance;
            if (!float.TryParse(
                    sections[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out distance)
                || distance < 0f)
            {
                return "'" + sections[1].Trim() + "' is not a distance. Give a number of 0 or more, in UV "
                    + "space.";
            }

            List<int> vertices;
            error = TryResolveVertices(mesh, sections.Length > 2 ? sections[2] : string.Empty, out vertices);
            if (error != null) return error;

            object result;
            RecordUndo(mesh, "Conduit Sew ProBuilder UVs");
            if (!TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.MeshOperations.UVEditing", "SewUVs",
                    new[] { typeof(ProBuilderMesh), typeof(int[]), typeof(float) },
                    new object[] { mesh, vertices.ToArray(), distance }, out result, out error))
            {
                return error;
            }

            MarkUvFacesManual(mesh, vertices);
            RebuildProBuilderMesh(mesh);
            return "Sewed the UVs of " + vertices.Count.ToString(CultureInfo.InvariantCulture)
                + " vertex position(s) that sat within "
                + distance.ToString("0.####", CultureInfo.InvariantCulture)
                + " of each other in UV space. Nothing outside that distance was joined, so a seam that "
                + "stays open needs a larger distance rather than a second call. The faces involved are "
                + "now on MANUAL UVs, without which the rebuild would have discarded this. "
                + DescribeProBuilderMesh(mesh) + " Positions are unchanged - this edits UVs only.";
        }

        /// <summary>
        /// Stitches one face's UVs onto another's along their shared edge, which is the UV editor's Auto
        /// Stitch. ProBuilder reports whether the two could be stitched at all, and that answer is passed on
        /// rather than assumed.
        /// </summary>
        /// <param name="args">Target, then the two face indexes.</param>
        /// <returns>Whether it stitched, or a validation error.</returns>
        private static string ProBuilderUvStitchCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 3)
            {
                return "Usage: probuilder-uv-stitch <target> | <faceA> | <faceB> [| channel:uv0|uv2]. Both "
                    + "faces are single indexes, and they must share an edge for there to be anything to "
                    + "stitch along.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            List<Face> first;
            List<int> firstIndexes;
            error = TryResolveFaces(mesh, sections[1], out first, out firstIndexes);
            if (error != null) return error;

            List<Face> second;
            List<int> secondIndexes;
            error = TryResolveFaces(mesh, sections[2], out second, out secondIndexes);
            if (error != null) return error;

            if (first.Count != 1 || second.Count != 1)
            {
                return "A stitch joins ONE face to ONE other, and "
                    + first.Count.ToString(CultureInfo.InvariantCulture) + " and "
                    + second.Count.ToString(CultureInfo.InvariantCulture) + " were named.";
            }

            if (firstIndexes[0] == secondIndexes[0])
            {
                return "Both indexes name face " + firstIndexes[0].ToString(CultureInfo.InvariantCulture)
                    + ", so there is nothing to stitch it to.";
            }

            int channel;
            error = TryResolveUvChannel(sections.Length > 3 ? sections[3] : string.Empty, out channel);
            if (error != null) return error;

            object result;
            RecordUndo(mesh, "Conduit Stitch ProBuilder UVs");
            if (!TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.MeshOperations.UVEditing", "AutoStitch",
                    new[] { typeof(ProBuilderMesh), typeof(Face), typeof(Face), typeof(int) },
                    new object[] { mesh, first[0], second[0], channel }, out result, out error))
            {
                return error;
            }

            var stitched = result is bool && (bool)result;
            if (!stitched)
            {
                return "ProBuilder could not stitch face "
                    + firstIndexes[0].ToString(CultureInfo.InvariantCulture) + " to face "
                    + secondIndexes[0].ToString(CultureInfo.InvariantCulture)
                    + ", and nothing was changed. A stitch needs the two faces to share an EDGE; "
                    + "probuilder-mesh-get with the faces section lists what each face is made of.";
            }

            RebuildProBuilderMesh(mesh);
            return "Stitched face " + secondIndexes[0].ToString(CultureInfo.InvariantCulture) + "'s "
                + UvChannelName(channel) + " onto face " + firstIndexes[0].ToString(CultureInfo.InvariantCulture)
                + "'s along their shared edge, so the two now read as one continuous island. "
                + DescribeProBuilderMesh(mesh) + " Positions are unchanged - this edits UVs only.";
        }

        /// <summary>
        /// Mirrors the named vertices' UVs about the centre of their own UV bounds, which is the UV editor's
        /// Flip U and Flip V. The UVs are split first, exactly as that action does, so a flip cannot drag a
        /// neighbouring island along with it.
        /// </summary>
        /// <param name="args">Target, the axis, then an optional vertex selector.</param>
        /// <returns>What was flipped, or a validation error.</returns>
        private static string ProBuilderUvFlipCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-uv-flip <target> | <axis:u|v> [| vertices] [| channel:uv0|uv2]. "
                    + "u mirrors left to right, v mirrors top to bottom, both about the centre of the "
                    + "selected UVs.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            var axis = sections[1].Trim().ToLowerInvariant();
            if (axis != "u" && axis != "v")
            {
                return "'" + axis + "' is not an axis. Use u or v.";
            }

            List<int> vertices;
            error = TryResolveVertices(mesh, sections.Length > 2 ? sections[2] : string.Empty, out vertices);
            if (error != null) return error;

            int channel;
            error = TryResolveUvChannel(sections.Length > 3 ? sections[3] : string.Empty, out channel);
            if (error != null) return error;

            Vector2[] existing;
            error = TryReadUvChannel(mesh, channel, out existing);
            if (error != null) return error;

            object result;
            RecordUndo(mesh, "Conduit Flip ProBuilder UVs");

            // ProBuilder's own Flip splits the UVs first so a mirror cannot drag a neighbouring island
            // with it. That step is UV0-only in ProBuilder, which refuses it on UV2, so UV2 mirrors what
            // is already there.
            if (channel == 0 && !TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.MeshOperations.UVEditing", "SplitUVs",
                    new[] { typeof(ProBuilderMesh), typeof(IEnumerable<int>) },
                    new object[] { mesh, vertices }, out result, out error))
            {
                return error;
            }

            // Read back AFTER the split, because splitting rewrites the array this then edits.
            Vector2[] working;
            error = TryReadUvChannel(mesh, channel, out working);
            if (error != null) return error;

            // The axis runs THROUGH the selection's own centre, which is what makes a flip stay where the
            // island already is instead of jumping across the UV square.
            var centre = UvSelectionCentre(working, vertices);
            foreach (var vertex in vertices)
            {
                if (vertex < 0 || vertex >= working.Length) continue;

                working[vertex] = axis == "u"
                    ? new Vector2(centre.x * 2f - working[vertex].x, working[vertex].y)
                    : new Vector2(working[vertex].x, centre.y * 2f - working[vertex].y);
            }

            error = ApplyUvChannel(mesh, working, channel, vertices);
            if (error != null) return error;

            // Verified rather than assumed. The first version of this verb reported a successful flip while
            // the rebuild had discarded every new UV, which is precisely the no-op-reported-as-success this
            // command contract forbids - so the written values are read back and counted.
            var held = CountUvsHeld(mesh, channel, vertices, working);
            if (held == 0)
            {
                return "The flip did NOT survive: ProBuilder regenerated the " + UvChannelName(channel)
                    + " of those " + vertices.Count.ToString(CultureInfo.InvariantCulture)
                    + " vertex position(s) and the mesh is as it was."
                    + (channel == 0
                        ? " Project the faces first with probuilder-uv-project, which puts them on manual "
                            + "UVs that a flip can edit."
                        : " A later probuilder-lightmap-uvs regenerates UV2 and would do the same.");
            }

            return "Flipped the " + UvChannelName(channel) + " of "
                + held.ToString(CultureInfo.InvariantCulture) + " of "
                + vertices.Count.ToString(CultureInfo.InvariantCulture) + " vertex position(s) in "
                + axis.ToUpperInvariant() + " about " + FormatVector2(centre)
                + ", the centre of their own UV bounds, and read the new coordinates back to confirm they "
                + "held. " + DescribeProBuilderMesh(mesh)
                + " Positions are unchanged - this edits UVs only.";
        }

        /// <summary>
        /// Switches faces between AUTO and MANUAL UVs, which is the UV editor's Set Auto UV and Set Manual
        /// UV. The flag decides whether a UV edit survives: an auto face recomputes its UVs from the auto
        /// panel on every refresh, so going back to auto DISCARDS whatever was hand-edited.
        /// </summary>
        /// <param name="args">Target, the mode, then an optional face selector.</param>
        /// <returns>What was switched, or a validation error.</returns>
        private static string ProBuilderUvModeCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-uv-mode <target> | <mode:auto|manual> [| faces]. auto recomputes "
                    + "the UVs from the auto panel and so discards hand edits; manual keeps what is there "
                    + "and stops probuilder-uv-set reaching them.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            var mode = sections[1].Trim().ToLowerInvariant();
            if (mode != "auto" && mode != "manual")
            {
                return "'" + mode + "' is not a mode. Use auto or manual.";
            }

            List<Face> faces;
            List<int> indexes;
            error = TryResolveFaces(
                mesh, sections.Length > 2 ? sections[2] : string.Empty, out faces, out indexes);
            if (error != null) return error;

            var alreadyManual = 0;
            foreach (var face in faces)
            {
                if (face.manualUV) alreadyManual++;
            }

            object result;
            RecordUndo(mesh, "Conduit Set ProBuilder UV Mode");
            if (!TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.UvUnwrapping", "SetAutoUV",
                    new[] { typeof(ProBuilderMesh), typeof(Face[]), typeof(bool) },
                    new object[] { mesh, faces.ToArray(), mode == "auto" }, out result, out error))
            {
                return error;
            }

            RebuildProBuilderMesh(mesh);

            var nowManual = 0;
            foreach (var face in faces)
            {
                if (face.manualUV) nowManual++;
            }

            return "Set " + indexes.Count.ToString(CultureInfo.InvariantCulture) + " face(s) to " + mode
                + " UVs - " + nowManual.ToString(CultureInfo.InvariantCulture) + " of them now read manual, "
                + "where " + alreadyManual.ToString(CultureInfo.InvariantCulture) + " did before. "
                + (mode == "auto"
                    ? "Going back to auto RECOMPUTES the UVs from the auto panel, so any hand editing on "
                        + "those faces is gone rather than hidden."
                    : "probuilder-uv-set no longer reaches these faces; the UV editing verbs do.")
                + " " + DescribeProBuilderMesh(mesh);
        }

        /// <summary>
        /// Fits the named vertices' UVs into the 0-1 square, which is the UV editor's Fit UVs. ProBuilder's
        /// own action skips a selection of fewer than three vertices, because two points describe no area to
        /// fit, and that refusal is passed on rather than reported as a fit.
        /// </summary>
        /// <param name="args">Target, then an optional vertex selector.</param>
        /// <returns>What was fitted, or a validation error.</returns>
        private static string ProBuilderUvFitCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<int> vertices;
            var error = TryResolveMeshAndUvVertices(sections, 1, out mesh, out vertices);
            if (error != null) return error;

            if (vertices.Count < 3)
            {
                return "A fit needs at least 3 vertex positions to have an area to fit, and "
                    + vertices.Count.ToString(CultureInfo.InvariantCulture)
                    + " were named. Nothing was changed.";
            }

            int channel;
            error = TryResolveUvChannel(sections.Length > 2 ? sections[2] : string.Empty, out channel);
            if (error != null) return error;

            Vector2[] working;
            error = TryReadUvChannel(mesh, channel, out working);
            if (error != null) return error;

            var subset = new Vector2[vertices.Count];
            for (var index = 0; index < vertices.Count; index++)
            {
                subset[index] = working[vertices[index]];
            }

            object result;
            RecordUndo(mesh, "Conduit Fit ProBuilder UVs");
            if (!TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.MeshOperations.UVEditing", "FitUVs",
                    new[] { typeof(Vector2[]) }, new object[] { subset }, out result, out error))
            {
                return error;
            }

            var fitted = result as Vector2[];
            if (fitted == null || fitted.Length != vertices.Count)
            {
                return "ProBuilder's FitUVs returned nothing usable, so nothing was changed.";
            }

            for (var index = 0; index < vertices.Count; index++)
            {
                working[vertices[index]] = fitted[index];
            }

            error = ApplyUvChannel(mesh, working, channel, vertices);
            if (error != null) return error;

            // Read back rather than assumed, for the same reason the flip does it.
            var held = CountUvsHeld(mesh, channel, vertices, working);
            if (held == 0)
            {
                return "The fit did NOT survive: ProBuilder regenerated those " + UvChannelName(channel)
                    + " coordinates and the mesh is as it was."
                    + (channel == 0
                        ? " Project the faces first with probuilder-uv-project."
                        : " A later probuilder-lightmap-uvs regenerates UV2 and would do the same.");
            }

            return "Fitted the " + UvChannelName(channel) + " of "
                + held.ToString(CultureInfo.InvariantCulture) + " of "
                + vertices.Count.ToString(CultureInfo.InvariantCulture)
                + " vertex position(s) into the 0-1 square, and read the new coordinates back to confirm "
                + "they held. " + DescribeProBuilderMesh(mesh)
                + " Positions are unchanged - this edits UVs only.";
        }

        /// <summary>Reads which UV channel a verb should act on, defaulting to UV0.</summary>
        /// <param name="section">The channel section, which may be absent.</param>
        /// <param name="channel">Receives 0 for UV0 or 1 for UV2.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string TryResolveUvChannel(string section, out int channel)
        {
            channel = 0;
            var wanted = (section ?? string.Empty).Trim().ToLowerInvariant();
            if (wanted.Length == 0 || wanted == "uv0") return null;
            if (wanted == "uv2")
            {
                channel = 1;
                return null;
            }

            return "'" + wanted + "' is not a UV channel. Use uv0 (the default) or uv2, the lightmap "
                + "channel.";
        }

        /// <summary>Names a UV channel for a message.</summary>
        /// <param name="channel">0 or 1.</param>
        /// <returns>The channel's display name.</returns>
        private static string UvChannelName(int channel)
        {
            return channel == 1 ? "UV2" : "UV0";
        }

        /// <summary>
        /// Reads one UV channel through ProBuilder's own accessor, which is what answers correctly for UV2 -
        /// the mesh's own textures property is UV0 alone.
        /// </summary>
        /// <param name="mesh">Mesh to read.</param>
        /// <param name="channel">Channel to read.</param>
        /// <param name="uvs">Receives the coordinates.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string TryReadUvChannel(ProBuilderMesh mesh, int channel, out Vector2[] uvs)
        {
            uvs = null;
            object result;
            string error;
            if (!TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.MeshOperations.UVEditing", "GetUVs",
                    new[] { typeof(ProBuilderMesh), typeof(int) },
                    new object[] { mesh, channel }, out result, out error))
            {
                return error;
            }

            uvs = result as Vector2[];
            if (uvs != null && uvs.Length == mesh.vertexCount) return null;

            uvs = null;
            return GetGameObjectPath(mesh.gameObject) + " carries no " + UvChannelName(channel)
                + " to edit. " + (channel == 1
                    ? "probuilder-lightmap-uvs generates UV2."
                    : "probuilder-uv-project lays UV0 out.");
        }

        /// <summary>
        /// Writes edited UVs back and settles the mesh, which differs by CHANNEL. Rebuilding regenerates an
        /// auto face's UV0 and also DISCARDS a UV2 edit - ProBuilder's own planar action calls ToMesh
        /// specifically to "Remove UV2 modifications" - so UV2 is written straight to the mesh and the
        /// rebuild is skipped, where UV0 needs the faces marked manual and the rebuild run.
        /// </summary>
        /// <param name="mesh">Mesh to write to.</param>
        /// <param name="uvs">The full coordinate array.</param>
        /// <param name="channel">Channel being written.</param>
        /// <param name="vertices">Vertices that were edited.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string ApplyUvChannel(
            ProBuilderMesh mesh, Vector2[] uvs, int channel, List<int> vertices)
        {
            object result;
            string error;
            if (!TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.MeshOperations.UVEditing", "ApplyUVs",
                    new[] { typeof(ProBuilderMesh), typeof(Vector2[]), typeof(int), typeof(bool) },
                    new object[] { mesh, uvs, channel, true }, out result, out error))
            {
                return error;
            }

            if (channel == 0)
            {
                MarkUvFacesManual(mesh, vertices);
                RebuildProBuilderMesh(mesh);
                return null;
            }

            EditorUtility.SetDirty(mesh);
            MarkGameObjectSceneDirty(mesh.gameObject);
            return null;
        }

        /// <summary>
        /// Counts how many of the written coordinates actually survived, which is the only thing separating
        /// a UV edit that took from one the mesh quietly regenerated away.
        /// </summary>
        /// <param name="mesh">Mesh to re-read.</param>
        /// <param name="channel">Channel that was written.</param>
        /// <param name="vertices">Vertices that were edited.</param>
        /// <param name="intended">The coordinates that were written.</param>
        /// <returns>How many vertices hold the intended coordinate.</returns>
        private static int CountUvsHeld(
            ProBuilderMesh mesh, int channel, List<int> vertices, Vector2[] intended)
        {
            Vector2[] settled;
            if (TryReadUvChannel(mesh, channel, out settled) != null) return 0;

            var held = 0;
            foreach (var vertex in vertices)
            {
                if (vertex < 0 || vertex >= intended.Length || vertex >= settled.Length) continue;
                if ((settled[vertex] - intended[vertex]).sqrMagnitude > 0.0000001f) continue;

                held++;
            }

            return held;
        }

        /// <summary>Centre of the UV bounds of the named vertices, which every pivot here is taken from.</summary>
        /// <param name="uvs">Coordinate array.</param>
        /// <param name="vertices">Vertices to measure.</param>
        /// <returns>The bounds centre.</returns>
        private static Vector2 UvSelectionCentre(Vector2[] uvs, List<int> vertices)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            var seen = false;
            foreach (var vertex in vertices)
            {
                if (vertex < 0 || vertex >= uvs.Length) continue;

                min = Vector2.Min(min, uvs[vertex]);
                max = Vector2.Max(max, uvs[vertex]);
                seen = true;
            }

            return seen ? (min + max) * 0.5f : Vector2.zero;
        }

        /// <summary>
        /// Moves, rotates and scales the named vertices' UVs about the centre of their own bounds. This is
        /// the UV editor's transform GIZMO, which is the one UV action with no callable entry of its own -
        /// the window applies it inline while dragging - so the arithmetic is here and ProBuilder's own
        /// ApplyUVs writes the result, the same division of labour as the flip and the fit.
        /// </summary>
        /// <param name="args">Target, a settings payload, then an optional vertex selector and channel.</param>
        /// <returns>What moved, or a validation error.</returns>
        private static string ProBuilderUvTransformCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-uv-transform <target> | <settingsJson> [| vertices] "
                    + "[| channel:uv0|uv2]. settingsJson takes offsetU, offsetV, rotation in degrees, "
                    + "scaleU and scaleV, all optional: {\"rotation\":45,\"scaleU\":2,\"scaleV\":2}.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            JObject settings;
            try { settings = JObject.Parse(sections[1]); }
            catch (Exception parseError)
            {
                return "The settings payload is not a JSON object: " + parseError.Message;
            }

            List<int> vertices;
            error = TryResolveVertices(mesh, sections.Length > 2 ? sections[2] : string.Empty, out vertices);
            if (error != null) return error;

            int channel;
            error = TryResolveUvChannel(sections.Length > 3 ? sections[3] : string.Empty, out channel);
            if (error != null) return error;

            var offset = new Vector2(PbFloat(settings, "offsetU", 0f), PbFloat(settings, "offsetV", 0f));
            var rotation = PbFloat(settings, "rotation", 0f);
            var scale = new Vector2(PbFloat(settings, "scaleU", 1f), PbFloat(settings, "scaleV", 1f));
            if (offset == Vector2.zero && rotation == 0f && scale == Vector2.one)
            {
                return "That payload asks for no change - offset 0, rotation 0, scale 1 - so nothing was "
                    + "written. Give at least one of offsetU, offsetV, rotation, scaleU or scaleV.";
            }

            Vector2[] working;
            error = TryReadUvChannel(mesh, channel, out working);
            if (error != null) return error;

            // Scale and rotation are taken about the selection's own centre so the island changes shape
            // where it sits; the offset is applied afterwards and moves it.
            var centre = UvSelectionCentre(working, vertices);
            var radians = rotation * Mathf.Deg2Rad;
            var cos = Mathf.Cos(radians);
            var sin = Mathf.Sin(radians);

            RecordUndo(mesh, "Conduit Transform ProBuilder UVs");
            foreach (var vertex in vertices)
            {
                if (vertex < 0 || vertex >= working.Length) continue;

                var local = working[vertex] - centre;
                local = new Vector2(local.x * scale.x, local.y * scale.y);
                local = new Vector2(local.x * cos - local.y * sin, local.x * sin + local.y * cos);
                working[vertex] = centre + local + offset;
            }

            error = ApplyUvChannel(mesh, working, channel, vertices);
            if (error != null) return error;

            var held = CountUvsHeld(mesh, channel, vertices, working);
            if (held == 0)
            {
                return "The transform did NOT survive: ProBuilder regenerated those "
                    + UvChannelName(channel) + " coordinates and the mesh is as it was."
                    + (channel == 0
                        ? " Project the faces first with probuilder-uv-project, which puts them on manual "
                            + "UVs an edit can hold."
                        : " A later probuilder-lightmap-uvs regenerates UV2 and would do the same.");
            }

            return "Transformed the " + UvChannelName(channel) + " of "
                + held.ToString(CultureInfo.InvariantCulture) + " of "
                + vertices.Count.ToString(CultureInfo.InvariantCulture) + " vertex position(s) about "
                + FormatVector2(centre) + " - scaled " + FormatVector2(scale) + ", rotated "
                + rotation.ToString("0.###", CultureInfo.InvariantCulture) + " degrees, then offset "
                + FormatVector2(offset) + " - and read the coordinates back to confirm they held. "
                + "Shared UVs move together unless probuilder-uv-split separated them first. "
                + DescribeProBuilderMesh(mesh) + " Positions are unchanged - this edits UVs only.";
        }

        /// <summary>
        /// Marks every face touching these vertices as MANUAL-UV. Without it the mesh rebuild regenerates
        /// an auto-UV face's UVs from its auto settings and discards the edit that was just written, which
        /// a verb has no way to notice: measured on a cube, an exported obj carried identical vt lines
        /// either side of a flip that had reported success.
        /// </summary>
        /// <param name="mesh">Mesh to mark.</param>
        /// <param name="vertices">Vertices whose faces were edited.</param>
        /// <returns>How many faces were marked.</returns>
        private static int MarkUvFacesManual(ProBuilderMesh mesh, List<int> vertices)
        {
            var touched = new HashSet<int>(vertices);
            var marked = 0;
            foreach (var face in mesh.faces)
            {
                foreach (var index in face.distinctIndexes)
                {
                    if (!touched.Contains(index)) continue;

                    face.manualUV = true;
                    marked++;
                    break;
                }
            }

            return marked;
        }

        /// <summary>Formats a UV coordinate for a confirmation line.</summary>
        /// <param name="value">Coordinate to format.</param>
        /// <returns>A compact description.</returns>
        private static string FormatVector2(Vector2 value)
        {
            return "(" + value.x.ToString("0.###", CultureInfo.InvariantCulture) + ", "
                + value.y.ToString("0.###", CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>
        /// Resolves the mesh and vertex set the UV operations share, so one shape of validation serves them
        /// all rather than being restated per verb.
        /// </summary>
        /// <param name="sections">Split command sections.</param>
        /// <param name="vertexSection">Index of the optional vertex selector.</param>
        /// <param name="mesh">Receives the mesh.</param>
        /// <param name="vertices">Receives the vertex indexes.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string TryResolveMeshAndUvVertices(
            string[] sections, int vertexSection, out ProBuilderMesh mesh, out List<int> vertices)
        {
            mesh = null;
            vertices = null;

            var error = TryFindProBuilderMesh(
                sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            return TryResolveVertices(
                mesh, sections.Length > vertexSection ? sections[vertexSection] : string.Empty, out vertices);
        }

        /// <summary>
        /// Cuts a face by handing ProBuilder's own Cut tool a path, which is the same data a click gives it:
        /// the face, a point on it, and the face normal. The tool classifies each point, rebuilds the face
        /// around the cut and executes, so none of that judgement is reproduced here.
        /// </summary>
        /// <param name="args">Target, the cut path, then an optional face selector.</param>
        /// <returns>What the cut produced, or a validation error.</returns>
        private static string ProBuilderCutCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-cut <target> | <points> [| face]. points is the cut path as "
                    + "'x y z, x y z, ...' with two or more points in the mesh's LOCAL space, and it goes "
                    + "after the pipe because it carries spaces and commas. Repeat the first point as the "
                    + "last to cut a closed shape out of the face. face defaults to the current selection "
                    + "and must resolve to exactly one face.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            List<Vector3> points;
            error = TryParsePolygonPoints(sections[1].Trim(), "cut path", 2, out points);
            if (error != null) return error;

            List<Face> faces;
            List<int> faceIndexes;
            error = TryResolveFaces(
                mesh, sections.Length > 2 ? sections[2] : string.Empty, out faces, out faceIndexes);
            if (error != null) return error;

            if (faces.Count != 1)
            {
                return "A cut runs across ONE face, and "
                    + faces.Count.ToString(CultureInfo.InvariantCulture) + " were named. Give a single face "
                    + "index, or select one, and cut the others separately.";
            }

            return RunProBuilderCut(mesh, faces[0], faceIndexes[0], points);
        }

        /// <summary>
        /// Drives ProBuilder's Cut tool across one face. The tool is activated through Unity's tool manager
        /// and handed its own SNAP mode, because without it every point classifies as NewVertex: a point
        /// sitting on an edge is then not recognised as one, the path reads as an open interior line, and
        /// the closure ProBuilder computes from it is degenerate. Measured on a cube's top face - with
        /// snapping on, two edge points came back classified AddedOnEdge and the face split in two; with it
        /// off the identical call threw inside ProBuilder's own rebuild.
        /// </summary>
        /// <param name="mesh">Mesh being cut.</param>
        /// <param name="face">Face the cut runs across.</param>
        /// <param name="faceIndex">That face's index, for the report.</param>
        /// <param name="points">Path points in the mesh's local space.</param>
        /// <returns>What the cut produced, or a user-facing error.</returns>
        private static string RunProBuilderCut(
            ProBuilderMesh mesh, Face face, int faceIndex, List<Vector3> points)
        {
            const System.Reflection.BindingFlags statics =
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic;

            var toolType = ProBuilderEditorType("UnityEditor.ProBuilder.CutTool");
            var manager = LoadedTypeByName("UnityEditor.EditorTools.ToolManager");
            var setActive = manager == null
                ? null
                : manager.GetMethod(
                    "SetActiveTool", statics, null,
                    new[] { typeof(UnityEditor.EditorTools.EditorTool) }, null);
            var update = toolType == null
                ? null
                : toolType.GetMethod(
                    "UpdateCurrentPosition", INSTANCE_MEMBERS, null,
                    new[] { typeof(Face), typeof(Vector3), typeof(Vector3) }, null);
            var addPoint = toolType == null ? null : toolType.GetMethod("AddCurrentPositionToPath", INSTANCE_MEMBERS);
            var execute = toolType == null ? null : toolType.GetMethod("ExecuteCut", INSTANCE_MEMBERS);
            if (setActive == null || update == null || addPoint == null || execute == null)
            {
                return "This build does not expose the entry points ProBuilder's Cut tool needs, so "
                    + "probuilder-cut is unavailable here. probuilder-insert-vertex-on-edge and "
                    + "probuilder-insert-vertex-in-face add cut points one at a time instead.";
            }

            var beforeFaces = mesh.faceCount;
            var beforeVerts = mesh.vertexCount;
            var normal = UnityEngine.ProBuilder.Math.Normal(mesh, face);
            var selected = Selection.objects;
            var tool = ScriptableObject.CreateInstance(toolType);
            var types = new StringBuilder();

            try
            {
                // The tool is only available for a SELECTED ProBuilder mesh, so selecting it is part of
                // activating it rather than a courtesy.
                Selection.objects = new UnityEngine.Object[] { mesh.gameObject };
                Selection.activeGameObject = mesh.gameObject;
                setActive.Invoke(null, new object[] { tool });

                var snapToGeometry = toolType.GetField("m_SnapToGeometry", INSTANCE_MEMBERS);
                if (snapToGeometry != null) snapToGeometry.SetValue(tool, true);
                var snappingPoint = toolType.GetField("m_SnappingPoint", INSTANCE_MEMBERS);
                if (snappingPoint != null) snappingPoint.SetValue(tool, true);

                var owner = toolType.GetField("m_Mesh", INSTANCE_MEMBERS);
                if (owner != null && owner.GetValue(tool) == null) owner.SetValue(tool, mesh);

                var addParameters = addPoint.GetParameters();
                foreach (var point in points)
                {
                    update.Invoke(tool, new object[] { face, point, normal });
                    addPoint.Invoke(tool, addParameters.Length == 0 ? new object[0] : new object[] { false });
                }

                // What ProBuilder decided each point IS, reported because it is the difference between a
                // cut that splits the face and one that leaves it whole.
                var pathField = toolType.GetField("m_CutPath", INSTANCE_MEMBERS);
                var path = pathField == null ? null : pathField.GetValue(tool);
                if (path != null)
                {
                    var count = (int)path.GetType().GetProperty("Count").GetValue(path, null);
                    var item = path.GetType().GetProperty("Item");
                    for (var entry = 0; entry < count; entry++)
                    {
                        if (entry > 0) types.Append('/');
                        var data = item.GetValue(path, new object[] { entry });
                        types.Append(Convert.ToString(data.GetType().GetProperty("types").GetValue(data, null)));
                    }
                }

                var executeParameters = execute.GetParameters();
                execute.Invoke(tool, executeParameters.Length == 0 ? new object[0] : new object[] { false });
            }
            finally
            {
                // Guarded so a cleanup failure cannot mask whatever the cut itself reported. Emptying the
                // path before disposing matters: disposing runs the tool's teardown, which executes the cut
                // a SECOND time, and an EditorTool made with CreateInstance is held by Unity's tool manager,
                // so a leaked one throws on every later compile until it is found and destroyed.
                try
                {
                    var restore = manager.GetMethod("RestorePreviousPersistentTool", statics)
                                  ?? manager.GetMethod("RestorePreviousTool", statics);
                    if (restore != null) restore.Invoke(null, null);

                    var pathField = toolType.GetField("m_CutPath", INSTANCE_MEMBERS);
                    var path = pathField == null ? null : pathField.GetValue(tool);
                    if (path != null) path.GetType().GetMethod("Clear").Invoke(path, null);
                    var target = toolType.GetField("m_TargetFace", INSTANCE_MEMBERS);
                    if (target != null) target.SetValue(tool, null);
                    var owner = toolType.GetField("m_Mesh", INSTANCE_MEMBERS);
                    if (owner != null) owner.SetValue(tool, null);
                }
                catch (Exception)
                {
                }

                UnityEngine.Object.DestroyImmediate(tool);
                Selection.objects = selected;
            }

            if (mesh.faceCount == beforeFaces)
            {
                return "ProBuilder's cut ran across face " + faceIndex.ToString(CultureInfo.InvariantCulture)
                    + " and split nothing - faces are still "
                    + beforeFaces.ToString(CultureInfo.InvariantCulture) + ". It classified the points as "
                    + types + ". Check the points lie ON that face - one that misses it has nothing to cut.";
            }

            return "Cut face " + faceIndex.ToString(CultureInfo.InvariantCulture) + " with "
                + points.Count.ToString(CultureInfo.InvariantCulture)
                + " point(s), which ProBuilder classified as " + types + ". faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + ", verts "
                + beforeVerts.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.vertexCount.ToString(CultureInfo.InvariantCulture) + ". "
                + DescribeProBuilderMesh(mesh)
                + " Face indexes have shifted; re-read them with probuilder-mesh-get. Collision does not "
                + "follow: run probuilder-colliders-rebuild.";
        }

        /// <summary>
        /// Adds a vertex inside a face and re-triangulates around it, which is what the Cut tool does for
        /// each point it is given. The extra edges it leaves are deliberate - they hold the new point in
        /// place, and merging the surrounding faces to tidy them up is what breaks the geometry.
        /// </summary>
        /// <param name="args">Target, a face index, then the point in local space.</param>
        /// <returns>What was inserted, or a validation error.</returns>
        private static string ProBuilderInsertVertexInFaceCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 3)
            {
                return "Usage: probuilder-insert-vertex-in-face <target> | <face> | <point>. face is ONE "
                    + "index and point is three numbers in the mesh's LOCAL space.";
            }

            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 1, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            if (faces.Count != 1)
            {
                return "This verb takes ONE face, and " + indexes.Count.ToString(CultureInfo.InvariantCulture)
                    + " were named. A point belongs to a single face, so name it explicitly.";
            }

            Vector3 point;
            if (!TryParseVector3(sections[2].Trim(), out point))
            {
                return "'" + sections[2].Trim() + "' is not three numbers. Write the point as 'x y z' in "
                    + "the mesh's local space.";
            }

            var beforeFaces = mesh.faceCount;
            var beforeVerts = mesh.vertexCount;
            RecordUndo(mesh, "Conduit Insert ProBuilder Vertex In Face");
            var rebuilt = AppendElements.InsertVertexInFace(mesh, faces[0], point);
            RebuildProBuilderMesh(mesh);

            if (rebuilt == null || rebuilt.Length == 0)
            {
                return "ProBuilder inserted nothing, and the mesh is unchanged. The point has to lie on the "
                    + "face named; one off its surface has nothing to split.";
            }

            return "Inserted a vertex at " + FormatVector3(point) + " into face "
                + indexes[0].ToString(CultureInfo.InvariantCulture) + ", which re-triangulated it into "
                + rebuilt.Length.ToString(CultureInfo.InvariantCulture) + " face(s). faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + ", verts "
                + beforeVerts.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.vertexCount.ToString(CultureInfo.InvariantCulture)
                + ". Those extra edges are deliberate and hold the point in place, so do NOT merge the "
                + "surrounding faces to remove them. Face indexes have shifted; re-read them with "
                + "probuilder-mesh-get.";
        }

        /// <summary>
        /// Adds a vertex part-way along an edge, splitting it without disturbing the faces either side.
        /// </summary>
        /// <param name="args">Target, an edge, then the point in local space.</param>
        /// <returns>What was inserted, or a validation error.</returns>
        private static string ProBuilderInsertVertexOnEdgeCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 3)
            {
                return "Usage: probuilder-insert-vertex-on-edge <target> | <edge> | <point>. edge is a "
                    + "vertex-index pair such as 0-1 and point is three numbers in the mesh's LOCAL space.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            List<Edge> edges;
            error = TryResolveEdges(mesh, sections[1], out edges);
            if (error != null) return error;

            if (edges.Count != 1)
            {
                return "This verb takes ONE edge, and " + edges.Count.ToString(CultureInfo.InvariantCulture)
                    + " were named. probuilder-subdivide-edges cuts several at even spacing instead.";
            }

            Vector3 point;
            if (!TryParseVector3(sections[2].Trim(), out point))
            {
                return "'" + sections[2].Trim() + "' is not three numbers. Write the point as 'x y z' in "
                    + "the mesh's local space.";
            }

            var beforeVerts = mesh.vertexCount;
            RecordUndo(mesh, "Conduit Insert ProBuilder Vertex On Edge");
            var inserted = AppendElements.InsertVertexOnEdge(mesh, edges[0], point);
            RebuildProBuilderMesh(mesh);

            if (mesh.vertexCount == beforeVerts)
            {
                return "ProBuilder inserted nothing, and the mesh is unchanged. The point has to lie on the "
                    + "edge named.";
            }

            return "Inserted a vertex on edge " + edges[0].a.ToString(CultureInfo.InvariantCulture) + "-"
                + edges[0].b.ToString(CultureInfo.InvariantCulture) + " at " + FormatVector3(point)
                + ", landing at " + FormatVector3(inserted.position) + ". verts "
                + beforeVerts.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.vertexCount.ToString(CultureInfo.InvariantCulture) + ", " + DescribeProBuilderMesh(mesh)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>Selects every face drawn with a given material, which is ProBuilder's Select Material.</summary>
        /// <param name="args">Target, then the material asset path.</param>
        /// <returns>Which faces matched, or a validation error.</returns>
        private static string ProBuilderSelectMaterialCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-select-material <target> | <materialPath>. assets-find with "
                    + "t:Material lists what this project has.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            var wanted = AssetDatabase.LoadAssetAtPath<Material>(sections[1].Trim());
            if (wanted == null)
            {
                return "No material asset at '" + sections[1].Trim() + "', so nothing was selected.";
            }

            var renderer = mesh.GetComponent<MeshRenderer>();
            var materials = renderer == null ? null : renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                return GetGameObjectPath(mesh.gameObject) + " has no materials, so no face can match.";
            }

            var matches = new List<Face>();
            var matchedIndexes = new List<int>();
            for (var index = 0; index < mesh.faceCount; index++)
            {
                var face = mesh.faces[index];
                var slot = face.submeshIndex;
                if (slot < 0 || slot >= materials.Length) continue;
                if (materials[slot] != wanted) continue;

                matches.Add(face);
                matchedIndexes.Add(index);
            }

            if (matches.Count == 0)
            {
                return "No face on " + GetGameObjectPath(mesh.gameObject) + " uses '" + wanted.name
                    + "'. The selection was left as it was.";
            }

            RecordUndo(mesh, "Conduit Select ProBuilder Material");
            mesh.SetSelectedFaces(matches);

            return "Selected " + matches.Count.ToString(CultureInfo.InvariantCulture) + " of "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + " face(s) drawn with '"
                + wanted.name + "': " + JoinIndexes(matchedIndexes) + ". The geometry was not changed.";
        }

        /// <summary>
        /// Selects every face sharing a smoothing group with the named ones, which is ProBuilder's Select
        /// Smoothing Group.
        /// </summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>Which faces matched, or a validation error.</returns>
        private static string ProBuilderSelectSmoothingGroupCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 1, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            // Group 0 means "in no group". Smoothing.smoothingGroupNone carries that value but is internal
            // to Unity.ProBuilder, so the number is written here rather than reached for.
            const int noSmoothingGroup = 0;

            var groups = new HashSet<int>();
            foreach (var face in faces)
            {
                if (face.smoothingGroup != noSmoothingGroup) groups.Add(face.smoothingGroup);
            }

            if (groups.Count == 0)
            {
                return "None of those " + indexes.Count.ToString(CultureInfo.InvariantCulture)
                    + " face(s) is in a smoothing group, so there is nothing to match. "
                    + "probuilder-smoothing-set puts faces in one.";
            }

            var matches = new List<Face>();
            var matchedIndexes = new List<int>();
            for (var index = 0; index < mesh.faceCount; index++)
            {
                if (!groups.Contains(mesh.faces[index].smoothingGroup)) continue;

                matches.Add(mesh.faces[index]);
                matchedIndexes.Add(index);
            }

            RecordUndo(mesh, "Conduit Select ProBuilder Smoothing Group");
            mesh.SetSelectedFaces(matches);

            return "Selected " + matches.Count.ToString(CultureInfo.InvariantCulture) + " of "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + " face(s) sharing smoothing group(s) "
                + JoinIndexes(groups) + ": " + JoinIndexes(matchedIndexes)
                + ". The geometry was not changed.";
        }

        /// <summary>
        /// Selects every face carrying a vertex colour found on the named ones, which is ProBuilder's
        /// Select by Colors.
        /// </summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>Which faces matched, or a validation error.</returns>
        private static string ProBuilderSelectVertexColorCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 1, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            // A mesh either carries one colour per vertex or no array at all. ProBuilder's own action
            // answers the no-array case by selecting every face of every COLOURLESS object, which is a
            // comparison between objects and has no meaning inside one mesh, so it is reported instead.
            var colors = mesh.colors;
            if (colors == null || colors.Count != mesh.vertexCount)
            {
                return GetGameObjectPath(mesh.gameObject) + " carries no vertex colours, so there is "
                    + "nothing to match by and the selection was left as it was. "
                    + "probuilder-vertex-color-set assigns them.";
            }

            // Quantised to Color32 because that is what ProBuilder's own action compares, so two colours
            // closer together than 1/255 per channel count as the same colour here too. Packed into a uint
            // rather than hashed as a Color32, which has no equality of its own and would fall back to
            // reflection over its fields.
            var sought = new HashSet<uint>();
            foreach (var face in faces)
            {
                foreach (var index in face.distinctIndexes) sought.Add(PackColor32(colors[index]));
            }

            var matches = new List<Face>();
            var matchedIndexes = new List<int>();
            for (var index = 0; index < mesh.faceCount; index++)
            {
                var face = mesh.faces[index];
                foreach (var vertex in face.distinctIndexes)
                {
                    if (!sought.Contains(PackColor32(colors[vertex]))) continue;

                    matches.Add(face);
                    matchedIndexes.Add(index);
                    break;
                }
            }

            RecordUndo(mesh, "Conduit Select ProBuilder Vertex Color");
            mesh.SetSelectedFaces(matches);

            return "Selected " + matches.Count.ToString(CultureInfo.InvariantCulture) + " of "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + " face(s) carrying any of the "
                + sought.Count.ToString(CultureInfo.InvariantCulture) + " colour(s) on the "
                + indexes.Count.ToString(CultureInfo.InvariantCulture) + " face(s) named: "
                + JoinIndexes(matchedIndexes) + ". A face matches when ANY of its vertices carries one of "
                + "those colours, so a face straddling a colour boundary is included. The geometry was not "
                + "changed.";
        }

        /// <summary>
        /// Moves the vertices behind whichever elements are named, which is ProBuilder's Offset Elements.
        /// The offset is in the mesh's LOCAL space, matching the other vertex verbs.
        /// </summary>
        /// <param name="args">Target, an offset, then an optional element selector.</param>
        /// <returns>What moved, or a validation error.</returns>
        private static string ProBuilderOffsetElementsCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-offset-elements <target> | <offset> [| elements]. offset is three "
                    + "numbers. elements is a vertex list, 'faces:0,1' or 'edges:0-1, 2-3', so one verb "
                    + "moves whichever element kind is meant.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            Vector3 offset;
            if (!TryParseVector3(sections[1].Trim(), out offset))
            {
                return "'" + sections[1].Trim() + "' is not three numbers. Write the offset as 'x y z'.";
            }

            var selector = sections.Length > 2 ? sections[2].Trim() : string.Empty;
            var vertices = new List<int>();

            if (selector.StartsWith("edges:", StringComparison.OrdinalIgnoreCase))
            {
                List<Edge> edges;
                error = TryResolveEdges(mesh, selector.Substring("edges:".Length), out edges);
                if (error != null) return error;

                var seen = new HashSet<int>();
                foreach (var edge in edges)
                {
                    if (seen.Add(edge.a)) vertices.Add(edge.a);
                    if (seen.Add(edge.b)) vertices.Add(edge.b);
                }
            }
            else if (selector.StartsWith("faces:", StringComparison.OrdinalIgnoreCase))
            {
                List<Face> faces;
                List<int> faceIndexes;
                error = TryResolveFaces(mesh, selector.Substring("faces:".Length), out faces, out faceIndexes);
                if (error != null) return error;

                var seen = new HashSet<int>();
                foreach (var face in faces)
                {
                    foreach (var index in face.distinctIndexes)
                    {
                        if (seen.Add(index)) vertices.Add(index);
                    }
                }
            }
            else
            {
                error = TryResolveVertices(mesh, selector, out vertices);
                if (error != null) return error;
            }

            if (vertices.Count == 0) return "Those elements resolved to no vertices, so nothing moved.";

            RecordUndo(mesh, "Conduit Offset ProBuilder Elements");
            mesh.TranslateVertices(vertices, offset);
            RebuildProBuilderMesh(mesh);

            return "Offset " + vertices.Count.ToString(CultureInfo.InvariantCulture) + " vertex position(s) by "
                + FormatVector3(offset) + " in LOCAL space. " + DescribeProBuilderMesh(mesh)
                + " Face and vertex counts are unchanged - only positions moved, so indexes still hold. "
                + "Collision does not follow: run probuilder-colliders-rebuild.";
        }

        /// <summary>
        /// Calls a static method on a type INTERNAL to Unity.ProBuilder, which is where the hole-finding
        /// and perimeter operations live. Mirrors the editor-assembly helper: the overload is pinned by
        /// exact parameter types, because several of these ship more than one and a name-only lookup throws
        /// before the call is attempted, and a missing type or method is named so an SDK change is
        /// attributable rather than silent.
        /// </summary>
        /// <param name="typeName">Full type name inside the runtime assembly.</param>
        /// <param name="methodName">Static method to call.</param>
        /// <param name="parameterTypes">Exact parameter types.</param>
        /// <param name="arguments">Arguments to pass.</param>
        /// <param name="result">Receives the return value.</param>
        /// <param name="error">Receives why the call could not be made.</param>
        /// <returns>True when the method ran.</returns>
        private static bool TryInvokeProBuilderRuntimeStatic(
            string typeName, string methodName, Type[] parameterTypes, object[] arguments,
            out object result, out string error)
        {
            result = null;
            error = null;

            var type = typeof(ProBuilderMesh).Assembly.GetType(typeName);
            if (type == null)
            {
                error = "This ProBuilder build has no " + typeName + ", so this action is unavailable.";
                return false;
            }

            var method = type.GetMethod(
                methodName,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic,
                null, parameterTypes, null);
            if (method == null)
            {
                error = "This ProBuilder build has no " + typeName + "." + methodName
                    + " with that signature, so this action is unavailable.";
                return false;
            }

            try { result = method.Invoke(null, arguments); return true; }
            catch (Exception invokeError) { error = "ProBuilder refused the call: " + Unwrap(invokeError); return false; }
        }

        /// <summary>
        /// Finds the open-edge loops bounding holes near the named vertices, which both the fill and the
        /// select verbs need. ElementSelection.FindHoles is internal, so it is reached by reflection.
        /// </summary>
        /// <param name="mesh">Mesh to search.</param>
        /// <param name="vertices">Vertices to search around.</param>
        /// <param name="holes">Receives one edge list per hole.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string TryFindProBuilderHoles(
            ProBuilderMesh mesh, List<int> vertices, out List<List<Edge>> holes)
        {
            holes = null;

            object found;
            string error;
            if (!TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.MeshOperations.ElementSelection", "FindHoles",
                    new[] { typeof(ProBuilderMesh), typeof(IEnumerable<int>) },
                    new object[] { mesh, vertices }, out found, out error))
            {
                return error;
            }

            holes = found as List<List<Edge>>;
            return holes != null
                ? null
                : "ProBuilder's hole search returned something this verb cannot read, so nothing was changed.";
        }

        /// <summary>Caps open holes with a new face, which is ProBuilder's Fill Hole.</summary>
        /// <param name="args">Target, then an optional vertex selector.</param>
        /// <returns>What was filled, or a validation error.</returns>
        private static string ProBuilderFillHoleCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(
                sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            List<int> vertices;
            error = TryResolveVertices(mesh, sections.Length > 1 ? sections[1] : PB_ALL_FACES, out vertices);
            if (error != null) return error;

            List<List<Edge>> holes;
            error = TryFindProBuilderHoles(mesh, vertices, out holes);
            if (error != null) return error;

            if (holes.Count == 0)
            {
                return "No hole borders those " + vertices.Count.ToString(CultureInfo.InvariantCulture)
                    + " vertices, so nothing was filled. A hole is a loop of OPEN edges; pass all to search "
                    + "the whole mesh.";
            }

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit Fill ProBuilder Hole");

            var filled = 0;
            var refused = 0;
            foreach (var hole in holes)
            {
                var ring = new List<int>();
                foreach (var edge in hole)
                {
                    if (!ring.Contains(edge.a)) ring.Add(edge.a);
                    if (!ring.Contains(edge.b)) ring.Add(edge.b);
                }

                if (ring.Count < 3)
                {
                    refused++;
                    continue;
                }

                // Ordered first, because a hole's edges already run around it; the unordered pass is
                // ProBuilder's own fallback for a ring its winding cannot be read from.
                var face = AppendElements.CreatePolygon(mesh, ring, false)
                    ?? AppendElements.CreatePolygon(mesh, ring, true);
                if (face != null) filled++;
                else refused++;
            }

            RebuildProBuilderMesh(mesh);

            if (filled == 0)
            {
                return "Found " + holes.Count.ToString(CultureInfo.InvariantCulture)
                    + " hole(s) but could not cap any of them, so the mesh is unchanged.";
            }

            return "Filled " + filled.ToString(CultureInfo.InvariantCulture) + " of "
                + holes.Count.ToString(CultureInfo.InvariantCulture) + " hole(s)"
                + (refused > 0 ? ", leaving " + refused.ToString(CultureInfo.InvariantCulture) + " that could not be capped" : string.Empty)
                + ". faces " + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + ", " + DescribeProBuilderMesh(mesh)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>Reports the open-edge loops bounding holes, which is ProBuilder's Select Hole.</summary>
        /// <param name="args">Target, then an optional vertex selector.</param>
        /// <returns>The holes found, or a validation error.</returns>
        private static string ProBuilderSelectHoleCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(
                sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            List<int> vertices;
            error = TryResolveVertices(mesh, sections.Length > 1 ? sections[1] : PB_ALL_FACES, out vertices);
            if (error != null) return error;

            List<List<Edge>> holes;
            error = TryFindProBuilderHoles(mesh, vertices, out holes);
            if (error != null) return error;

            if (holes.Count == 0)
            {
                return "No hole borders those " + vertices.Count.ToString(CultureInfo.InvariantCulture)
                    + " vertices. Pass all to search the whole mesh. Nothing was changed.";
            }

            var report = new StringBuilder();
            report.Append(GetGameObjectPath(mesh.gameObject)).Append(": ")
                .Append(holes.Count.ToString(CultureInfo.InvariantCulture)).Append(" hole(s)");

            var edgeSelection = new List<Edge>();
            for (var hole = 0; hole < holes.Count; hole++)
            {
                report.Append(Environment.NewLine).Append("  hole ")
                    .Append(hole.ToString(CultureInfo.InvariantCulture)).Append(": ")
                    .Append(holes[hole].Count.ToString(CultureInfo.InvariantCulture)).Append(" edge(s) ");

                for (var edge = 0; edge < holes[hole].Count; edge++)
                {
                    if (edge > 0) report.Append(", ");
                    report.Append(holes[hole][edge].a.ToString(CultureInfo.InvariantCulture)).Append('-')
                        .Append(holes[hole][edge].b.ToString(CultureInfo.InvariantCulture));
                    edgeSelection.Add(holes[hole][edge]);
                }
            }

            RecordUndo(mesh, "Conduit Select ProBuilder Hole");
            mesh.SetSelectedEdges(edgeSelection);

            report.Append(Environment.NewLine)
                .Append("Those edges are now the mesh's edge selection, so probuilder-fill-hole, ")
                .Append("probuilder-extrude-edges and probuilder-bevel-edges can act on them without ")
                .Append("naming pairs. The geometry was not changed.");
            return report.ToString();
        }

        /// <summary>Pulls a selection in from its perimeter, which is ProBuilder's Shrink Selection.</summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>What the selection became, or a validation error.</returns>
        private static string ProBuilderShrinkSelectionCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 1, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            object found;
            if (!TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.MeshOperations.ElementSelection", "GetPerimeterFaces",
                    new[] { typeof(ProBuilderMesh), typeof(IEnumerable<Face>) },
                    new object[] { mesh, faces }, out found, out error))
            {
                return error;
            }

            var perimeter = new HashSet<Face>();
            var perimeterFaces = found as IEnumerable<Face>;
            if (perimeterFaces != null)
            {
                foreach (var face in perimeterFaces) perimeter.Add(face);
            }

            var kept = new List<Face>();
            var keptIndexes = new List<int>();
            for (var i = 0; i < faces.Count; i++)
            {
                if (perimeter.Contains(faces[i])) continue;

                kept.Add(faces[i]);
                keptIndexes.Add(indexes[i]);
            }

            if (kept.Count == 0)
            {
                return "Shrinking would empty the selection: every one of those "
                    + indexes.Count.ToString(CultureInfo.InvariantCulture)
                    + " face(s) is on its perimeter, so nothing is left once the border comes off. The "
                    + "selection was left as it was.";
            }

            RecordUndo(mesh, "Conduit Shrink ProBuilder Selection");
            mesh.SetSelectedFaces(kept);

            return "Shrank the selection from " + indexes.Count.ToString(CultureInfo.InvariantCulture)
                + " to " + kept.Count.ToString(CultureInfo.InvariantCulture) + " face(s) by dropping the "
                + (indexes.Count - kept.Count).ToString(CultureInfo.InvariantCulture)
                + " on its perimeter. Now selected: " + JoinIndexes(keptIndexes)
                + ". The geometry was not changed.";
        }

        /// <summary>
        /// Runs a new edge loop around the ring an edge belongs to, which is ProBuilder's Insert Edge Loop.
        /// </summary>
        /// <param name="args">Target, then an optional edge selector.</param>
        /// <returns>What was inserted, or a validation error.</returns>
        private static string ProBuilderInsertEdgeLoopCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(
                sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            List<Edge> edges;
            error = TryResolveEdges(mesh, sections.Length > 1 ? sections[1] : string.Empty, out edges);
            if (error != null) return error;

            object found;
            if (!TryInvokeProBuilderRuntimeStatic(
                    "UnityEngine.ProBuilder.MeshOperations.ElementSelection", "GetEdgeRing",
                    new[] { typeof(ProBuilderMesh), typeof(IEnumerable<Edge>) },
                    new object[] { mesh, edges }, out found, out error))
            {
                return error;
            }

            var ring = new List<Edge>();
            var ringEdges = found as IEnumerable<Edge>;
            if (ringEdges != null)
            {
                foreach (var edge in ringEdges) ring.Add(edge);
            }

            if (ring.Count == 0)
            {
                return "Those edges belong to no ring, so no loop could be inserted and the mesh is "
                    + "unchanged. A ring runs across quads; a triangle fan has none.";
            }

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit Insert ProBuilder Edge Loop");
            var connected = mesh.Connect(ring);
            RebuildProBuilderMesh(mesh);

            var inserted = connected.item2 == null ? 0 : connected.item2.Length;
            if (inserted == 0)
            {
                return "ProBuilder inserted no loop, and the mesh is unchanged.";
            }

            mesh.SetSelectedEdges(connected.item2);

            return "Inserted an edge loop across the ring of " + ring.Count.ToString(CultureInfo.InvariantCulture)
                + " edge(s), adding " + inserted.ToString(CultureInfo.InvariantCulture)
                + " edge(s) which are now the edge selection. faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + ", " + DescribeProBuilderMesh(mesh)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>
        /// Turns an edge selector into concrete edges. An edge is a PAIR of vertex indexes joined by a
        /// hyphen and pairs are separated by commas - "0-1, 2-3" - because a comma inside an edge could
        /// not be told apart from the separator between them.
        /// </summary>
        /// <param name="mesh">Mesh the indexes address.</param>
        /// <param name="selector">"selected", or a comma separated list of hyphenated index pairs.</param>
        /// <param name="edges">Receives the resolved edges.</param>
        /// <returns>Null on success, or a user-facing error naming the offending pair.</returns>
        private static string TryResolveEdges(ProBuilderMesh mesh, string selector, out List<Edge> edges)
        {
            edges = new List<Edge>();

            var wanted = (selector ?? string.Empty).Trim();
            if (wanted.Length == 0) wanted = PB_SELECTED_FACES;

            if (string.Equals(wanted, PB_SELECTED_FACES, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var selected in mesh.selectedEdges) edges.Add(selected);
                return edges.Count > 0
                    ? null
                    : "No edges are selected on this mesh. Name them as vertex-index pairs instead, e.g. "
                        + "'0-1, 2-3'.";
            }

            // "faces:0,1" means every edge belonging to those faces, which is how a whole face is bevelled
            // or extruded without naming each of its edges by hand.
            if (wanted.StartsWith("faces:", StringComparison.OrdinalIgnoreCase))
            {
                List<Face> faces;
                List<int> faceIndexes;
                var faceError = TryResolveFaces(
                    mesh, wanted.Substring("faces:".Length), out faces, out faceIndexes);
                if (faceError != null) return faceError;

                var fromFaces = new HashSet<Edge>();
                foreach (var face in faces)
                {
                    foreach (var edge in face.edges)
                    {
                        if (fromFaces.Add(edge)) edges.Add(edge);
                    }
                }

                return edges.Count > 0 ? null : "Those faces carry no edges.";
            }

            foreach (var token in wanted.Split(','))
            {
                var trimmed = token.Trim();
                if (trimmed.Length == 0) continue;

                // The pair parser takes a comma between the two indexes, so the hyphen form is translated
                // rather than parsed twice over.
                Edge edge;
                string edgeError;
                if (!TryParseEdge(trimmed.Replace('-', ','), mesh, out edge, out edgeError)) return edgeError;

                edges.Add(edge);
            }

            return edges.Count > 0 ? null : "No edges were named.";
        }

        /// <summary>Merges vertices into one, which is ProBuilder's Collapse Vertices.</summary>
        /// <param name="args">Target, then an optional collapse point and vertex selector.</param>
        /// <returns>What was collapsed, or a validation error.</returns>
        private static string ProBuilderCollapseVerticesCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(
                sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            var toFirst = false;
            if (sections.Length > 1 && sections[1].Trim().Length > 0)
            {
                var where = sections[1].Trim().ToLowerInvariant();
                if (where == "first") toFirst = true;
                else if (where != "center")
                {
                    return "'" + sections[1].Trim() + "' is not a collapse point. Use center (the default, "
                        + "as ProBuilder defaults) or first.";
                }
            }

            List<int> vertices;
            error = TryResolveVertices(mesh, sections.Length > 2 ? sections[2] : string.Empty, out vertices);
            if (error != null) return error;

            if (vertices.Count < 2)
            {
                return "Collapsing needs at least two vertices, and "
                    + vertices.Count.ToString(CultureInfo.InvariantCulture) + " was named.";
            }

            var beforeVerts = mesh.vertexCount;
            RecordUndo(mesh, "Conduit Collapse ProBuilder Vertices");
            var survivor = mesh.MergeVertices(vertices.ToArray(), toFirst);
            RebuildProBuilderMesh(mesh);

            if (survivor < 0)
            {
                return "ProBuilder refused to collapse those "
                    + vertices.Count.ToString(CultureInfo.InvariantCulture) + " vertices, and the mesh is "
                    + "unchanged. They have to belong to the same mesh and not already be one vertex.";
            }

            return "Collapsed " + vertices.Count.ToString(CultureInfo.InvariantCulture)
                + " vertices into index " + survivor.ToString(CultureInfo.InvariantCulture) + ", at their "
                + (toFirst ? "FIRST vertex" : "shared CENTRE") + ". verts "
                + beforeVerts.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.vertexCount.ToString(CultureInfo.InvariantCulture) + ", " + DescribeProBuilderMesh(mesh)
                + ". Vertex and face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>Runs a new edge between vertices on a face, which is ProBuilder's Connect Vertices.</summary>
        /// <param name="args">Target, then an optional vertex selector.</param>
        /// <returns>What was connected, or a validation error.</returns>
        private static string ProBuilderConnectVerticesCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(
                sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            List<int> vertices;
            error = TryResolveVertices(mesh, sections.Length > 1 ? sections[1] : string.Empty, out vertices);
            if (error != null) return error;

            if (vertices.Count < 2)
            {
                return "Connecting needs at least two vertices, and "
                    + vertices.Count.ToString(CultureInfo.InvariantCulture) + " was named.";
            }

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit Connect ProBuilder Vertices");
            var split = mesh.Connect(vertices);
            RebuildProBuilderMesh(mesh);

            if (split == null || split.Length == 0)
            {
                return "ProBuilder connected nothing, and the mesh is unchanged. Connecting runs an edge "
                    + "ACROSS a face, so the vertices have to sit on a shared face and not already be "
                    + "joined by an edge.";
            }

            return "Connected " + vertices.Count.ToString(CultureInfo.InvariantCulture)
                + " vertices, splitting their faces and leaving "
                + split.Length.ToString(CultureInfo.InvariantCulture) + " new vertex index(es). faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + ", " + DescribeProBuilderMesh(mesh)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>Pulls new geometry out of open edges, which is ProBuilder's Extrude Edges.</summary>
        /// <param name="args">Target, then a distance, an optional edge selector and options.</param>
        /// <returns>What was extruded, or a validation error.</returns>
        private static string ProBuilderExtrudeEdgesCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2)
            {
                return "Usage: probuilder-extrude-edges <target> | <distance> [| edges] [| optionsJson]. "
                    + "edges is selected (the default) or vertex-index pairs such as '0-1, 2-3'. Options: "
                    + "asGroup, allowNonManifold.";
            }

            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(sections[0].Trim(), out mesh);
            if (error != null) return error;

            float distance;
            if (!float.TryParse(
                    sections[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out distance))
            {
                return "'" + sections[1].Trim() + "' is not a distance.";
            }

            List<Edge> edges;
            error = TryResolveEdges(mesh, sections.Length > 2 ? sections[2] : string.Empty, out edges);
            if (error != null) return error;

            JObject options = null;
            if (sections.Length > 3 && sections[3].Trim().Length > 0)
            {
                try { options = JObject.Parse(sections[3]); }
                catch (Exception parseError)
                {
                    return "The options payload is not a JSON object: " + parseError.Message;
                }
            }

            // Grouped by default, so a selected run of edges lifts as one skirt rather than each edge
            // pulling its own disconnected quad.
            var asGroup = PbBool(options, "asGroup", true);
            var allowNonManifold = PbBool(options, "allowNonManifold", false);

            var beforeFaces = mesh.faceCount;
            RecordUndo(mesh, "Conduit Extrude ProBuilder Edges");
            var newEdges = mesh.Extrude(edges, distance, asGroup, allowNonManifold);
            RebuildProBuilderMesh(mesh);

            if (newEdges == null || newEdges.Length == 0)
            {
                return "ProBuilder extruded nothing, and the mesh is unchanged. An edge has to be OPEN to "
                    + "extrude - one already between two faces has nowhere to go unless allowNonManifold "
                    + "is set.";
            }

            return "Extruded " + edges.Count.ToString(CultureInfo.InvariantCulture) + " edge(s) by "
                + distance.ToString("0.###", CultureInfo.InvariantCulture) + (asGroup ? " as a group" : " individually")
                + ", producing " + newEdges.Length.ToString(CultureInfo.InvariantCulture) + " new edge(s). faces "
                + beforeFaces.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.faceCount.ToString(CultureInfo.InvariantCulture) + ", " + DescribeProBuilderMesh(mesh)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>Adds vertices along edges, which is ProBuilder's Subdivide Edges.</summary>
        /// <param name="args">Target, then an optional subdivision count and edge selector.</param>
        /// <returns>What was subdivided, or a validation error.</returns>
        private static string ProBuilderSubdivideEdgesCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            var error = TryFindProBuilderMesh(
                sections.Length > 0 ? sections[0].Trim() : string.Empty, out mesh);
            if (error != null) return error;

            // One cut, matching ProBuilder's own default, and its editor caps the slider at 32.
            var subdivisions = 1;
            if (sections.Length > 1 && sections[1].Trim().Length > 0)
            {
                if (!int.TryParse(
                        sections[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out subdivisions))
                {
                    return "'" + sections[1].Trim() + "' is not a subdivision count.";
                }

                if (subdivisions < 1 || subdivisions > 32)
                {
                    return "A subdivision count runs from 1 to 32, which is the range ProBuilder's own "
                        + "editor allows; " + subdivisions.ToString(CultureInfo.InvariantCulture) + " is outside it.";
                }
            }

            List<Edge> edges;
            error = TryResolveEdges(mesh, sections.Length > 2 ? sections[2] : string.Empty, out edges);
            if (error != null) return error;

            var beforeVerts = mesh.vertexCount;
            RecordUndo(mesh, "Conduit Subdivide ProBuilder Edges");
            var newEdges = AppendElements.AppendVerticesToEdge(mesh, edges, subdivisions);
            RebuildProBuilderMesh(mesh);

            if (newEdges == null || newEdges.Count == 0)
            {
                return "ProBuilder subdivided nothing, and the mesh is unchanged.";
            }

            return "Subdivided " + edges.Count.ToString(CultureInfo.InvariantCulture) + " edge(s) with "
                + subdivisions.ToString(CultureInfo.InvariantCulture) + " cut(s) each, producing "
                + newEdges.Count.ToString(CultureInfo.InvariantCulture) + " edge(s). verts "
                + beforeVerts.ToString(CultureInfo.InvariantCulture) + " -> "
                + mesh.vertexCount.ToString(CultureInfo.InvariantCulture) + ", " + DescribeProBuilderMesh(mesh)
                + ". Face indexes have shifted; re-read them with probuilder-mesh-get.";
        }

        /// <summary>
        /// Turns the dividing edge inside a quad the other way, which is ProBuilder's Flip Face Edge. It
        /// changes how a quad is triangulated without moving a single vertex.
        /// </summary>
        /// <param name="args">Target, then an optional face selector.</param>
        /// <returns>How many faces flipped, or a validation error.</returns>
        private static string ProBuilderFlipFaceEdgeCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            ProBuilderMesh mesh;
            List<Face> faces;
            List<int> indexes;
            var error = TryResolveMeshAndFaces(sections, 1, PB_SELECTED_FACES, out mesh, out faces, out indexes);
            if (error != null) return error;

            RecordUndo(mesh, "Conduit Flip ProBuilder Face Edge");

            var flipped = 0;
            foreach (var face in faces)
            {
                if (SurfaceTopology.FlipEdge(mesh, face)) flipped++;
            }

            RebuildProBuilderMesh(mesh);

            if (flipped == 0)
            {
                return "None of those " + indexes.Count.ToString(CultureInfo.InvariantCulture)
                    + " face(s) had an edge to flip, so the mesh is unchanged. Only a QUAD has a dividing "
                    + "edge to turn; a triangle and an n-gon have none.";
            }

            return "Flipped the dividing edge on " + flipped.ToString(CultureInfo.InvariantCulture)
                + " of " + indexes.Count.ToString(CultureInfo.InvariantCulture)
                + " face(s), which re-triangulates them without moving any vertex. "
                + DescribeProBuilderMesh(mesh);
        }

        private static string DispatchProBuilder(string verb, string command, string[] parts)
        {
            if (verb == "probuilder-shape-create")
            {
                return ProBuilderShapeCreateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-mesh-get")
            {
                return ProBuilderMeshGetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-select")
            {
                return ProBuilderSelectCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-face-extrude")
            {
                return ProBuilderFaceExtrudeCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-face-delete")
            {
                return ProBuilderFaceDeleteCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-subdivide")
            {
                return ProBuilderSubdivideCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-uv-set")
            {
                return ProBuilderUvSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-uv-project")
            {
                return ProBuilderUvProjectCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-uv-split")
            {
                return ProBuilderUvSplitCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-uv-collapse")
            {
                return ProBuilderUvCollapseCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-uv-sew")
            {
                return ProBuilderUvSewCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-uv-stitch")
            {
                return ProBuilderUvStitchCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-uv-flip")
            {
                return ProBuilderUvFlipCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-uv-mode")
            {
                return ProBuilderUvModeCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-uv-fit")
            {
                return ProBuilderUvFitCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-uv-transform")
            {
                return ProBuilderUvTransformCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-face-material")
            {
                return ProBuilderFaceMaterialCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-vertex-color-set")
            {
                return ProBuilderVertexColorSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-smoothing-set")
            {
                return ProBuilderSmoothingSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-grow-selection")
            {
                return ProBuilderSelectGrowCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-select-loop")
            {
                return ProBuilderSelectLoopCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-select-flood")
            {
                return ProBuilderSelectFloodCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-bevel-edges")
            {
                return ProBuilderBevelEdgesCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-face-merge")
            {
                return ProBuilderFaceMergeCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-face-detach")
            {
                return ProBuilderFaceDetachCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-face-duplicate")
            {
                return ProBuilderFaceDuplicateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-face-duplicate-flip")
            {
                return ProBuilderFaceDuplicateFlipCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-flip-normals")
            {
                return ProBuilderNormalsFlipCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-conform-normals")
            {
                return ProBuilderNormalsConformCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-recalculate-normals")
            {
                return ProBuilderNormalsRecalculateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-to-quads")
            {
                return ProBuilderToQuadsCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-triangulate")
            {
                return ProBuilderToTrianglesCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-vert-weld")
            {
                return ProBuilderVertexWeldCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-vert-split")
            {
                return ProBuilderVertexSplitCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-vert-translate")
            {
                return ProBuilderVertexTranslateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-edge-bridge")
            {
                return ProBuilderEdgeBridgeCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-edge-connect")
            {
                return ProBuilderEdgeConnectCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-set-pivot")
            {
                return ProBuilderPivotSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-freeze-transform")
            {
                return ProBuilderFreezeTransformCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-lightmap-uvs")
            {
                return ProBuilderOptimizeCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-colliders-rebuild")
            {
                return ProBuilderCollidersRebuildCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-cleanup")
            {
                return ProBuilderCleanupCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-validate")
            {
                return ProBuilderValidateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-merge-objects")
            {
                return ProBuilderCombineCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-mirror-objects")
            {
                return ProBuilderMirrorCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-export")
            {
                return ProBuilderExportCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-strip")
            {
                return ProBuilderStripCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-poly-create")
            {
                return ProBuilderPolyCreateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-bezier-create")
            {
                return ProBuilderBezierCreateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-ize")
            {
                return ProBuilderizeCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-fit-to-size")
            {
                return ProBuilderFitToSizeCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-lightmap-static")
            {
                return ProBuilderLightmapStaticCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-collapse-vertices")
            {
                return ProBuilderCollapseVerticesCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-connect-vertices")
            {
                return ProBuilderConnectVerticesCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-extrude-edges")
            {
                return ProBuilderExtrudeEdgesCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-subdivide-edges")
            {
                return ProBuilderSubdivideEdgesCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-flip-face-edge")
            {
                return ProBuilderFlipFaceEdgeCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-fill-hole")
            {
                return ProBuilderFillHoleCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-select-hole")
            {
                return ProBuilderSelectHoleCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-shrink-selection")
            {
                return ProBuilderShrinkSelectionCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-insert-edge-loop")
            {
                return ProBuilderInsertEdgeLoopCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-select-material")
            {
                return ProBuilderSelectMaterialCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-select-vertex-color")
            {
                return ProBuilderSelectVertexColorCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-xray")
            {
                return ProBuilderXRayCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-vert-set-position")
            {
                return ProBuilderVertSetPositionCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-select-edge-loop")
            {
                return ProBuilderSelectEdgeLoopCommand(GetCommandRemainder(command, parts[0]), false);
            }

            if (verb == "probuilder-select-edge-ring")
            {
                return ProBuilderSelectEdgeLoopCommand(GetCommandRemainder(command, parts[0]), true);
            }

            if (verb == "probuilder-select-smoothing-group")
            {
                return ProBuilderSelectSmoothingGroupCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-offset-elements")
            {
                return ProBuilderOffsetElementsCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-cut")
            {
                return ProBuilderCutCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-insert-vertex-in-face")
            {
                return ProBuilderInsertVertexInFaceCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-insert-vertex-on-edge")
            {
                return ProBuilderInsertVertexOnEdgeCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "probuilder-set-collider")
            {
                return SetProBuilderCollisionVolume(GetCommandRemainder(command, parts[0]), false);
            }

            if (verb == "probuilder-set-trigger")
            {
                return SetProBuilderCollisionVolume(GetCommandRemainder(command, parts[0]), true);
            }

            return null;
        }

        #endregion
    }
}

#endif
