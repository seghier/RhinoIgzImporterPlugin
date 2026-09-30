using System;
using System.IO;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.FileIO;
using Rhino.PlugIns;

[assembly: Guid("8E001AC5-667A-43C5-A008-81ED5DFB2BEC")]

namespace RhinoIgzImporter
{
    /// <summary>
    /// Registers .igz as a recognized file type so Rhino's File → Import
    /// and drag-and-drop can open IngeTrazo documents directly.
    /// The main PlugIn class must inherit from FileImportPlugIn instead of PlugIn
    /// to register file types.
    /// </summary>
    public class RhinoIgzImporterPlugin : FileImportPlugIn
    {
        public static RhinoIgzImporterPlugin Instance { get; private set; }

        public RhinoIgzImporterPlugin()
        {
            Instance = this;
        }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            RhinoApp.WriteLine("RhinoIgzImporter — IngeTrazo .igz import plugin loaded.");
            return LoadReturnCode.Success;
        }

        protected override FileTypeList AddFileTypes(FileReadOptions options)
        {
            var types = new FileTypeList();
            types.AddFileType("IngeTrazo Document (*.igz)", "igz");
            return types;
        }

        protected override bool ReadFile(string filename, int index, RhinoDoc doc, FileReadOptions options)
        {
            try
            {
                RhinoApp.WriteLine($"Importing IngeTrazo file: {Path.GetFileName(filename)}");

                var igzDoc = IgzDocument.Load(filename);

                RhinoApp.WriteLine($"  Format: v{igzDoc.FormatVersion}, App version: {igzDoc.AppVersion}");
                if (igzDoc.EmbeddedTextures.Count > 0)
                    RhinoApp.WriteLine($"  Embedded textures: {igzDoc.EmbeddedTextures.Count}");

                var importOptions = new IgzImportOptions
                {
                    ImportHidden = false,
                    CreateRhinoGroups = true,
                    ImportEdges = true
                };

                var builder = new IgzSceneBuilder(doc, igzDoc, importOptions);
                int count = builder.Build();

                RhinoApp.WriteLine($"  Created {count} object(s).");

                // Zoom extents after import
                doc.Views.ActiveView?.ActiveViewport.ZoomExtents();
                doc.Views.Redraw();

                return true;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error importing .igz: {ex.Message}");
                return false;
            }
        }
    }
}
