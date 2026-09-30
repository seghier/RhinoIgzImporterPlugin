using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RhinoIgzImporter
{
    /// <summary>
    /// Parsed IGZ document: the raw JSON payload plus any embedded texture files on disk.
    /// Format 1 = plain JSON on disk. Format 2 = ZIP container (document.json + textures/).
    /// </summary>
    public sealed class IgzDocument
    {
        private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 }; // "PK\x03\x04"
        private const string DocEntry = "document.json";

        public int FormatVersion { get; private set; }
        public string AppVersion { get; private set; }
        public JObject ScenePayload { get; private set; }
        public Dictionary<string, byte[]> EmbeddedTextures { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> TextureDiskPaths { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        private IgzDocument() { }

        /// <summary>
        /// Open an .igz file from disk — either plain JSON or a ZIP container.
        /// Extracts any embedded textures to a local cache directory so Rhino can load them.
        /// </summary>
        public static IgzDocument Load(string filePath)
        {
            var raw = File.ReadAllBytes(filePath);
            bool isZip = raw.Length >= 4
                         && raw[0] == ZipMagic[0] && raw[1] == ZipMagic[1]
                         && raw[2] == ZipMagic[2] && raw[3] == ZipMagic[3];

            string jsonText;
            var textures = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var diskPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RhinoIgzImporter", "textures");

            if (isZip)
            {
                Directory.CreateDirectory(cacheDir);

                using var ms = new MemoryStream(raw);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

                var docEntry = zip.GetEntry(DocEntry)
                               ?? throw new InvalidOperationException(
                                   $"Not an IngeTrazo document: no {DocEntry} inside the archive.");
                using (var reader = new StreamReader(docEntry.Open(), Encoding.UTF8))
                {
                    jsonText = reader.ReadToEnd();
                }

                // Extract embedded textures to disk
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)
                        && entry.Length > 0)
                    {
                        using var entryStream = entry.Open();
                        using var buf = new MemoryStream();
                        entryStream.CopyTo(buf);
                        byte[] bytes = buf.ToArray();
                        textures[entry.FullName] = bytes;

                        string fileName = Path.GetFileName(entry.FullName);
                        string targetPath = Path.Combine(cacheDir, fileName);

                        try
                        {
                            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length != bytes.Length)
                            {
                                File.WriteAllBytes(targetPath, bytes);
                            }
                        }
                        catch
                        {
                            // If file in use, try temporary fallback
                            targetPath = Path.Combine(Path.GetTempPath(), fileName);
                            File.WriteAllBytes(targetPath, bytes);
                        }

                        diskPaths[entry.FullName] = targetPath;
                        diskPaths[fileName] = targetPath;
                    }
                }
            }
            else
            {
                jsonText = Encoding.UTF8.GetString(raw);
            }

            var root = JObject.Parse(jsonText);
            var doc = new IgzDocument
            {
                FormatVersion = root.Value<int>("igz_format"),
                AppVersion = root.Value<string>("app_version") ?? "unknown",
                ScenePayload = root.Value<JObject>("scene") ?? new JObject(),
                EmbeddedTextures = textures,
                TextureDiskPaths = diskPaths
            };
            return doc;
        }

        /// <summary>
        /// Resolve an embedded or disk texture path to a local existing image file.
        /// </summary>
        public string ResolveTexturePath(string refPath)
        {
            if (string.IsNullOrWhiteSpace(refPath)) return null;

            if (TextureDiskPaths.TryGetValue(refPath, out string cached) && File.Exists(cached))
                return cached;

            string fileName = Path.GetFileName(refPath);
            if (TextureDiskPaths.TryGetValue(fileName, out cached) && File.Exists(cached))
                return cached;

            if (File.Exists(refPath))
                return refPath;

            return null;
        }
    }
}
