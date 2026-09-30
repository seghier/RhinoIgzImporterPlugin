using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoIgzImporter
{
    public class MaterialEntry
    {
        public string Name;
        public Color DiffuseColor = Color.White;
        public double Opacity = 1.0;
        public string TexturePath;
        public double Sw = 1.0;
        public double Sh = 1.0;
        public double Rot = 0.0;
        public int RhinoMaterialIndex = -1;
        public Rhino.Render.RenderMaterial RhinoRenderMaterial;
    }

    public class IgzImportOptions
    {
        public bool ImportHidden { get; set; } = false;
        public bool CreateRhinoGroups { get; set; } = true;
        public bool ImportEdges { get; set; } = true;
    }

    /// <summary>
    /// Converts an IngeTrazo .igz document into Rhino geometry, materials, textures, and annotations.
    /// </summary>
    public sealed class IgzSceneBuilder
    {
        private readonly RhinoDoc _doc;
        private readonly IgzDocument _igzDoc;
        private readonly JObject _payload;
        private readonly IgzImportOptions _options;

        // Layer name → Rhino layer index
        private readonly Dictionary<string, int> _layerMap = new(StringComparer.OrdinalIgnoreCase);

        // Material key / name → MaterialEntry
        private readonly Dictionary<string, MaterialEntry> _materialMap = new(StringComparer.OrdinalIgnoreCase);

        // Prototype index → Prototype mesh definition
        private JObject[] _protoPayloads;

        // Scale factor: IGZ default unit is metres
        private double _unitScale = 1.0;
        private string _lengthUnit = "m";

        // All created object Guids
        private readonly List<Guid> _createdIds = new();

        public IgzSceneBuilder(RhinoDoc doc, IgzDocument igzDoc, IgzImportOptions options)
        {
            _doc = doc;
            _igzDoc = igzDoc;
            _payload = igzDoc.ScenePayload;
            _options = options;
        }

        public int Build()
        {
            DetermineUnitScale();
            BuildLayers();
            BuildMaterials();
            CachePrototypes();

            // Build loose geometry (loose edges and faces at root)
            BuildEdges(_payload, Transform.Identity, "Default");
            BuildFaces(_payload, Transform.Identity, "Default", false);

            // Build groups (which include components, sub-groups, billboards)
            var groups = _payload["groups"] as JArray;
            if (groups != null)
            {
                foreach (var g in groups)
                {
                    if (g is JObject groupObj)
                        BuildGroup(groupObj, Transform.Identity, null);
                }
            }

            // Build dimensions as real Rhino LinearDimension annotations + display curves
            BuildDimensions();

            _doc.Views.Redraw();
            return _createdIds.Count;
        }

        private void DetermineUnitScale()
        {
            var units = _payload["units"] as JObject;
            _lengthUnit = units?.Value<string>("length") ?? "m";

            var rhinoUnits = _doc.ModelUnitSystem;
            var igzUnits = ParseUnitSystem(_lengthUnit);
            _unitScale = RhinoMath.UnitScale(igzUnits, rhinoUnits);
        }

        private static UnitSystem ParseUnitSystem(string unit)
        {
            return unit?.ToLowerInvariant() switch
            {
                "m" or "meters" or "metres" => UnitSystem.Meters,
                "cm" or "centimeters" => UnitSystem.Centimeters,
                "mm" or "millimeters" => UnitSystem.Millimeters,
                "in" or "inches" => UnitSystem.Inches,
                "ft" or "feet" => UnitSystem.Feet,
                "yd" or "yards" => UnitSystem.Yards,
                _ => UnitSystem.Meters,
            };
        }

        #region Layers

        private void BuildLayers()
        {
            var layers = _payload["layers"] as JArray;
            if (layers == null) return;

            foreach (var raw in layers)
            {
                string name = raw.Value<string>("name") ?? "Default";
                if (_layerMap.ContainsKey(name)) continue;

                int existing = _doc.Layers.FindByFullPath(name, -1);
                if (existing >= 0)
                {
                    _layerMap[name] = existing;
                    continue;
                }

                var layer = new Layer { Name = name };
                var color = raw["color"] as JArray;
                if (color != null && color.Count >= 3)
                {
                    layer.Color = Color.FromArgb(
                        ClampByte(color[0].Value<double>()),
                        ClampByte(color[1].Value<double>()),
                        ClampByte(color[2].Value<double>()));
                }

                if (raw.Value<bool?>("visible") == false)
                    layer.IsVisible = false;
                if (raw.Value<bool?>("locked") == true)
                    layer.IsLocked = true;

                int idx = _doc.Layers.Add(layer);
                if (idx >= 0)
                    _layerMap[name] = idx;
            }
        }

        private int ResolveLayerIndex(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
                return _doc.Layers.CurrentLayer.Index;

            if (_layerMap.TryGetValue(layerName, out int idx))
                return idx;

            int existing = _doc.Layers.FindByFullPath(layerName, -1);
            if (existing >= 0)
            {
                _layerMap[layerName] = existing;
                return existing;
            }

            var layer = new Layer { Name = layerName };
            int newIdx = _doc.Layers.Add(layer);
            if (newIdx >= 0)
                _layerMap[layerName] = newIdx;
            return newIdx >= 0 ? newIdx : _doc.Layers.CurrentLayer.Index;
        }

        #endregion

        #region Materials & Textures

        private void BuildMaterials()
        {
            var mats = _payload["materials"] as JArray;
            if (mats == null) return;

            foreach (var raw in mats)
            {
                string name = raw.Value<string>("name");
                if (string.IsNullOrEmpty(name) || _materialMap.ContainsKey(name)) continue;

                var entry = ParseMaterialEntry(name, raw as JObject);
                RegisterMaterial(entry);
            }
        }

        private MaterialEntry ParseMaterialEntry(string name, JObject raw)
        {
            var entry = new MaterialEntry { Name = name };

            var color = raw?["color"] as JArray;
            if (color != null && color.Count >= 3)
            {
                entry.DiffuseColor = Color.FromArgb(
                    ClampByte(color[0].Value<double>()),
                    ClampByte(color[1].Value<double>()),
                    ClampByte(color[2].Value<double>()));
            }

            var opacity = raw?.Value<double?>("opacity");
            if (opacity.HasValue)
                entry.Opacity = Math.Clamp(opacity.Value, 0.0, 1.0);

            var tex = raw?["texture"] as JObject;
            if (tex != null)
            {
                entry.Sw = tex.Value<double?>("sw") ?? 1.0;
                entry.Sh = tex.Value<double?>("sh") ?? 1.0;
                entry.Rot = tex.Value<double?>("rot") ?? 0.0;

                string embed = tex.Value<string>("embed") ?? tex.Value<string>("path");
                entry.TexturePath = _igzDoc.ResolveTexturePath(embed);

                // If no explicit color was defined, sample representative color from the texture bitmap
                if (color == null && !string.IsNullOrEmpty(entry.TexturePath))
                {
                    entry.DiffuseColor = GetAverageImageColor(entry.TexturePath);
                }
            }

            return entry;
        }

        private void RegisterMaterial(MaterialEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Name)) return;

            // Check if material already exists in Rhino document
            int existing = _doc.Materials.Find(entry.Name, true);
            if (existing >= 0)
            {
                entry.RhinoMaterialIndex = existing;
                _materialMap[entry.Name] = entry;
                return;
            }

            var rhinoMat = new Rhino.DocObjects.Material
            {
                Name = entry.Name,
                DiffuseColor = entry.DiffuseColor,
                Transparency = 1.0 - entry.Opacity
            };

            if (!string.IsNullOrEmpty(entry.TexturePath) && File.Exists(entry.TexturePath))
            {
                rhinoMat.SetBitmapTexture(entry.TexturePath);
            }

            int idx = _doc.Materials.Add(rhinoMat);
            if (idx >= 0)
            {
                entry.RhinoMaterialIndex = idx;
                var addedMat = _doc.Materials[idx];

                // Create Rhino 8 RenderMaterial for the modern Materials Panel
                try
                {
                    var renderMat = Rhino.Render.RenderMaterial.CreateBasicMaterial(addedMat, _doc);
                    if (renderMat != null)
                    {
                        _doc.RenderMaterials.Add(renderMat);
                        entry.RhinoRenderMaterial = renderMat;
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"Notice: RenderMaterial registration for '{entry.Name}': {ex.Message}");
                }
            }

            _materialMap[entry.Name] = entry;
        }

        private MaterialEntry GetOrCreateFaceMaterial(JObject face)
        {
            if (face == null) return null;

            string matName = face.Value<string>("mat");
            if (!string.IsNullOrEmpty(matName) && _materialMap.TryGetValue(matName, out var existing))
                return existing;

            var tex = face["texture"] as JObject;
            var color = face["color"] as JArray;

            // If face has inline texture or color without named material, create an ad-hoc material
            if (tex != null || color != null)
            {
                string inlineName = matName;
                if (string.IsNullOrEmpty(inlineName))
                {
                    string embed = tex?.Value<string>("embed") ?? tex?.Value<string>("path");
                    if (!string.IsNullOrEmpty(embed))
                        inlineName = Path.GetFileNameWithoutExtension(embed);
                    else
                        inlineName = $"Mat_{Guid.NewGuid().ToString("N")[..6]}";
                }

                if (_materialMap.TryGetValue(inlineName, out var cached))
                    return cached;

                var entry = ParseMaterialEntry(inlineName, face);
                RegisterMaterial(entry);
                return entry;
            }

            return null;
        }

        private static Color GetAverageImageColor(string imagePath)
        {
            try
            {
                if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                    return Color.LightGray;

                using var bmp = new System.Drawing.Bitmap(imagePath);
                long r = 0, g = 0, b = 0, count = 0;
                int stepX = Math.Max(1, bmp.Width / 16);
                int stepY = Math.Max(1, bmp.Height / 16);

                for (int y = 0; y < bmp.Height; y += stepY)
                {
                    for (int x = 0; x < bmp.Width; x += stepX)
                    {
                        var pixel = bmp.GetPixel(x, y);
                        if (pixel.A > 32)
                        {
                            r += pixel.R;
                            g += pixel.G;
                            b += pixel.B;
                            count++;
                        }
                    }
                }
                if (count > 0)
                    return Color.FromArgb((int)(r / count), (int)(g / count), (int)(b / count));
            }
            catch { }
            return Color.LightGray;
        }

        #endregion

        #region Prototypes

        private void CachePrototypes()
        {
            var protos = _payload["protos"] as JArray;
            if (protos == null)
            {
                _protoPayloads = Array.Empty<JObject>();
                return;
            }

            _protoPayloads = new JObject[protos.Count];
            for (int i = 0; i < protos.Count; i++)
            {
                _protoPayloads[i] = protos[i] as JObject;
            }
        }

        #endregion

        #region Edges

        private void BuildEdges(JObject meshPayload, Transform xform, string defaultLayer)
        {
            if (!_options.ImportEdges) return;

            var edges = meshPayload?["edges"] as JArray;
            if (edges == null) return;

            foreach (var raw in edges)
            {
                var a = ParsePoint3d(raw["a"] as JArray);
                var b = ParsePoint3d(raw["b"] as JArray);
                if (!a.IsValid || !b.IsValid || a.DistanceTo(b) < 1e-12) continue;

                a *= _unitScale;
                b *= _unitScale;

                var line = new LineCurve(a, b);
                line.Transform(xform);

                bool hidden = raw.Value<bool?>("hidden") == true;
                if (hidden && !_options.ImportHidden) continue;

                string edgeLayer = raw.Value<string>("layer") ?? defaultLayer;

                var attrs = new ObjectAttributes();
                attrs.LayerIndex = ResolveLayerIndex(edgeLayer);
                if (hidden) attrs.Visible = false;

                if (raw.Value<bool?>("soft") == true)
                    attrs.ColorSource = ObjectColorSource.ColorFromObject;

                var id = _doc.Objects.AddCurve(line, attrs);
                if (id != Guid.Empty) _createdIds.Add(id);
            }
        }

        #endregion

        #region Faces & Texture Mapping

        private void BuildFaces(JObject meshPayload, Transform xform, string defaultLayer, bool isBillboard)
        {
            var faces = meshPayload?["faces"] as JArray;
            if (faces == null || faces.Count == 0) return;

            // Group faces by (Layer, Material) so each mesh receives its own native material and layer
            var groups = new Dictionary<(string Layer, MaterialEntry Mat), List<JObject>>();

            foreach (var f in faces)
            {
                if (f is not JObject faceObj) continue;

                if (faceObj.Value<bool?>("hidden") == true && !_options.ImportHidden)
                    continue;

                string layer = faceObj.Value<string>("layer") ?? defaultLayer ?? "Default";
                var mat = GetOrCreateFaceMaterial(faceObj);

                var key = (layer, mat);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<JObject>();
                    groups[key] = list;
                }
                list.Add(faceObj);
            }

            foreach (var kvp in groups)
            {
                string layerName = kvp.Key.Layer;
                var matEntry = kvp.Key.Mat;
                var faceList = kvp.Value;

                var mesh = new Mesh();

                foreach (var faceObj in faceList)
                {
                    var verts = faceObj["vertices"] as JArray;
                    if (verts == null || verts.Count < 3) continue;

                    var ptsInMeters = new List<Point3d>();
                    foreach (var v in verts)
                    {
                        var pt = ParsePoint3d(v as JArray);
                        if (pt.IsValid) ptsInMeters.Add(pt);
                    }
                    if (ptsInMeters.Count < 3) continue;

                    // Compute face normal in meters
                    Vector3d normal = ComputeNormal(ptsInMeters);

                    // Compute texture UVs
                    List<Point2f> uvs = new();
                    if (isBillboard && ptsInMeters.Count == 4)
                    {
                        // Billboard quad corners: (0,0), (1,0), (1,1), (0,1)
                        uvs.Add(new Point2f(0f, 0f));
                        uvs.Add(new Point2f(1f, 0f));
                        uvs.Add(new Point2f(1f, 1f));
                        uvs.Add(new Point2f(0f, 1f));
                    }
                    else if (matEntry != null)
                    {
                        double sw = matEntry.Sw;
                        double sh = matEntry.Sh;
                        double rot = matEntry.Rot;

                        // Per-face texture override
                        var texOverride = faceObj["texture"] as JObject;
                        if (texOverride != null)
                        {
                            sw = texOverride.Value<double?>("sw") ?? sw;
                            sh = texOverride.Value<double?>("sh") ?? sh;
                            rot = texOverride.Value<double?>("rot") ?? rot;
                        }

                        var (uAxis, vAxis) = GetProjectionAxes(normal, rot);
                        sw = Math.Abs(sw) > 1e-9 ? sw : 1.0;
                        sh = Math.Abs(sh) > 1e-9 ? sh : 1.0;

                        foreach (var p in ptsInMeters)
                        {
                            double u = (p.X * uAxis.X + p.Y * uAxis.Y + p.Z * uAxis.Z) / sw;
                            double v = (p.X * vAxis.X + p.Y * vAxis.Y + p.Z * vAxis.Z) / sh;
                            uvs.Add(new Point2f((float)u, (float)v));
                        }
                    }

                    int baseIdx = mesh.Vertices.Count;
                    for (int i = 0; i < ptsInMeters.Count; i++)
                    {
                        var scaledPt = ptsInMeters[i] * _unitScale;
                        mesh.Vertices.Add(scaledPt);
                        mesh.Normals.Add(normal);

                        if (uvs.Count > i)
                            mesh.TextureCoordinates.Add(uvs[i]);
                    }

                    // Fan triangulation for n-gon faces
                    for (int i = 1; i < ptsInMeters.Count - 1; i++)
                    {
                        mesh.Faces.AddFace(baseIdx, baseIdx + i, baseIdx + i + 1);
                    }
                }

                if (mesh.Faces.Count == 0) continue;

                mesh.Compact();
                mesh.Transform(xform);

                var attrs = new ObjectAttributes();
                attrs.LayerIndex = ResolveLayerIndex(layerName);

                if (matEntry != null)
                {
                    attrs.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                    attrs.MaterialIndex = matEntry.RhinoMaterialIndex;
                    if (matEntry.RhinoRenderMaterial != null)
                    {
                        attrs.RenderMaterial = matEntry.RhinoRenderMaterial;
                    }
                }

                var id = _doc.Objects.AddMesh(mesh, attrs);
                if (id != Guid.Empty) _createdIds.Add(id);
            }
        }

        private static Vector3d ComputeNormal(List<Point3d> pts)
        {
            if (pts.Count < 3) return Vector3d.ZAxis;

            Vector3d normal = Vector3d.Zero;
            for (int i = 0; i < pts.Count; i++)
            {
                var cur = pts[i];
                var next = pts[(i + 1) % pts.Count];
                normal.X += (cur.Y - next.Y) * (cur.Z + next.Z);
                normal.Y += (cur.Z - next.Z) * (cur.X + next.X);
                normal.Z += (cur.X - next.X) * (cur.Y + next.Y);
            }

            if (normal.Unitize() && normal.IsValid)
                return normal;

            Vector3d e1 = pts[1] - pts[0];
            Vector3d e2 = pts[2] - pts[0];
            normal = Vector3d.CrossProduct(e1, e2);
            if (normal.Unitize() && normal.IsValid)
                return normal;

            return Vector3d.ZAxis;
        }

        /// <summary>
        /// IngeTrazo .skp-compatible projection basis formula for planar texture mapping.
        /// </summary>
        public static (Vector3d uAxis, Vector3d vAxis) GetProjectionAxes(Vector3d normal, double rotDeg = 0.0)
        {
            const double VERTICAL_TOLERANCE = 1e-3;
            double nx = normal.X, ny = normal.Y, nz = normal.Z;
            double ln = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (ln > 1e-30) { nx /= ln; ny /= ln; nz /= ln; }

            double xx = -ny, xy = nx; // Z x n
            double lx = Math.Sqrt(xx * xx + xy * xy);
            Vector3d xr, yr;
            if (lx < VERTICAL_TOLERANCE)
            {
                xr = nz > 0 ? new Vector3d(1, 0, 0) : new Vector3d(-1, 0, 0);
                yr = new Vector3d(0, 1, 0);
            }
            else
            {
                xx /= lx; xy /= lx;
                xr = new Vector3d(xx, xy, 0.0);
                yr = new Vector3d(-nz * xy, nz * xx, nx * xy - ny * xx);
            }

            if (Math.Abs(rotDeg) > 1e-6)
            {
                double rad = RhinoMath.ToRadians(rotDeg);
                double cosA = Math.Cos(rad), sinA = Math.Sin(rad);
                Vector3d u = xr * cosA + yr * sinA;
                Vector3d v = yr * cosA - xr * sinA;
                xr = u; yr = v;
            }

            return (xr, yr);
        }

        #endregion

        #region Groups & Components

        private void BuildGroup(JObject groupObj, Transform parentXform, string parentLayer)
        {
            if (groupObj == null) return;

            string name = groupObj.Value<string>("name");
            string layerName = groupObj.Value<string>("layer") ?? parentLayer;
            bool hidden = groupObj.Value<bool?>("hidden") == true;
            bool isBillboard = groupObj.Value<bool?>("billboard") == true;

            if (hidden && !_options.ImportHidden) return;

            Transform localXform = Transform.Identity;
            var xfArray = groupObj["xform"] as JArray;
            if (xfArray != null && xfArray.Count == 16)
            {
                localXform = ParseColumnMajorTransform(xfArray);
            }

            Transform combinedXform = parentXform * localXform;

            int preCount = _createdIds.Count;

            // Is this a component referencing a prototype?
            int protoIdx = groupObj.Value<int?>("proto") ?? -1;
            if (protoIdx >= 0 && protoIdx < _protoPayloads.Length && _protoPayloads[protoIdx] != null)
            {
                var proto = _protoPayloads[protoIdx];
                BuildEdges(proto, combinedXform, layerName);
                BuildFaces(proto, combinedXform, layerName, isBillboard);
            }
            else
            {
                BuildEdges(groupObj, combinedXform, layerName);
                BuildFaces(groupObj, combinedXform, layerName, isBillboard);
            }

            // Recurse into children
            var children = groupObj["children"] as JArray;
            if (children != null)
            {
                foreach (var child in children)
                {
                    if (child is JObject childObj)
                        BuildGroup(childObj, combinedXform, layerName);
                }
            }

            // Group objects in Rhino if requested
            int newCount = _createdIds.Count - preCount;
            if (newCount > 1 && _options.CreateRhinoGroups)
            {
                int grpIdx = _doc.Groups.Add(name ?? "IgzGroup");
                for (int i = preCount; i < _createdIds.Count; i++)
                {
                    var obj = _doc.Objects.FindId(_createdIds[i]);
                    if (obj != null)
                    {
                        var a = obj.Attributes;
                        a.AddToGroup(grpIdx);
                        obj.CommitChanges();
                    }
                }
            }
        }

        private static Transform ParseColumnMajorTransform(JArray data)
        {
            double[] vals = new double[16];
            for (int i = 0; i < 16 && i < data.Count; i++)
                vals[i] = data[i].Value<double>();

            var xf = Transform.Identity;
            for (int col = 0; col < 4; col++)
                for (int row = 0; row < 4; row++)
                    xf[row, col] = vals[col * 4 + row];

            return xf;
        }

        #endregion

        #region Dimensions

        private void BuildDimensions()
        {
            var dims = _payload["dimensions"] as JArray;
            if (dims == null || dims.Count == 0) return;

            foreach (var raw in dims)
            {
                var ptA_raw = ParsePoint3d(raw["a"] as JArray);
                var ptB_raw = ParsePoint3d(raw["b"] as JArray);
                var offset_raw = ParseVector3d(raw["offset"] as JArray);

                if (!ptA_raw.IsValid || !ptB_raw.IsValid) continue;

                Point3d ptA = ptA_raw * _unitScale;
                Point3d ptB = ptB_raw * _unitScale;
                Vector3d vOffset = offset_raw * _unitScale;

                string layerName = raw.Value<string>("layer") ?? "dimensions";
                int layerIdx = ResolveLayerIndex(layerName);

                Vector3d vDir = ptB - ptA;
                double length = vDir.Length;
                if (length < 1e-6) continue;

                // Dimension line points
                Point3d linePtA = ptA + vOffset;
                Point3d linePtB = ptB + vOffset;

                string text = raw.Value<string>("text");
                if (string.IsNullOrEmpty(text))
                {
                    double measuredLength = ptA_raw.DistanceTo(ptB_raw);
                    text = $"{measuredLength:F2} {_lengthUnit}";
                }

                // 1. Try to create native Rhino LinearDimension
                bool dimensionCreated = false;
                try
                {
                    Vector3d vNormal = Vector3d.CrossProduct(vDir, vOffset);
                    if (vNormal.Length < 1e-6)
                    {
                        vNormal = Vector3d.CrossProduct(vDir, Vector3d.ZAxis);
                        if (vNormal.Length < 1e-6)
                            vNormal = Vector3d.CrossProduct(vDir, Vector3d.YAxis);
                    }
                    vNormal.Unitize();

                    Vector3d xAxis = vDir;
                    xAxis.Unitize();

                    Vector3d yAxis = Vector3d.CrossProduct(vNormal, xAxis);
                    yAxis.Unitize();
                    if (vOffset * yAxis < 0)
                    {
                        yAxis = -yAxis;
                        vNormal = -vNormal;
                    }

                    Plane dimPlane = new Plane(ptA, xAxis, yAxis);
                    var dimStyle = _doc.DimStyles.Current;

                    var linearDim = LinearDimension.Create(
                        AnnotationType.Aligned,
                        dimStyle,
                        dimPlane,
                        xAxis,
                        ptA,
                        ptB,
                        linePtA,
                        0.0
                    );

                    if (linearDim != null)
                    {
                        if (!string.IsNullOrEmpty(raw.Value<string>("text")))
                            linearDim.PlainText = text;

                        var dimAttrs = new ObjectAttributes { LayerIndex = layerIdx };
                        var dimId = _doc.Objects.AddLinearDimension(linearDim, dimAttrs);
                        if (dimId != Guid.Empty)
                        {
                            _createdIds.Add(dimId);
                            dimensionCreated = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"LinearDimension creation notice: {ex.Message}");
                }

                // 2. Also create clean visual dimension lines + text dot so dimensions are crystal clear
                // in every viewport mode (Wireframe, Shaded, Rendered)
                var lineAttrs = new ObjectAttributes { LayerIndex = layerIdx };

                // The main dimension line
                var dimLine = new LineCurve(linePtA, linePtB);
                var lid = _doc.Objects.AddCurve(dimLine, lineAttrs);
                if (lid != Guid.Empty) _createdIds.Add(lid);

                // Extension lines (witness lines) from measured points to dimension line
                var extA = new LineCurve(ptA, linePtA);
                var extB = new LineCurve(ptB, linePtB);
                _doc.Objects.AddCurve(extA, lineAttrs);
                _doc.Objects.AddCurve(extB, lineAttrs);

                // If native LinearDimension wasn't created, place a TextDot showing the measurement value
                if (!dimensionCreated)
                {
                    Point3d midPt = (linePtA + linePtB) * 0.5;
                    _doc.Objects.AddTextDot(text, midPt, lineAttrs);
                }
            }
        }

        #endregion

        #region Helpers

        private static Point3d ParsePoint3d(JArray arr)
        {
            if (arr == null || arr.Count < 3)
                return Point3d.Unset;

            double x = arr[0].Value<double>();
            double y = arr[1].Value<double>();
            double z = arr[2].Value<double>();

            if (!RhinoMath.IsValidDouble(x) || !RhinoMath.IsValidDouble(y) || !RhinoMath.IsValidDouble(z))
                return Point3d.Unset;

            return new Point3d(x, y, z);
        }

        private static Vector3d ParseVector3d(JArray arr)
        {
            if (arr == null || arr.Count < 3)
                return Vector3d.Zero;

            double x = arr[0].Value<double>();
            double y = arr[1].Value<double>();
            double z = arr[2].Value<double>();

            if (!RhinoMath.IsValidDouble(x) || !RhinoMath.IsValidDouble(y) || !RhinoMath.IsValidDouble(z))
                return Vector3d.Zero;

            return new Vector3d(x, y, z);
        }

        private static int ClampByte(double v)
        {
            if (v <= 1.0 && v >= 0.0)
                return (int)(v * 255.0);
            return Math.Clamp((int)v, 0, 255);
        }

        #endregion
    }
}
