using System;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;

namespace RhinoIgzImporter
{
    /// <summary>
    /// Command: ImportIgz — opens a file dialog for .igz files and imports
    /// the IngeTrazo 3D model into the active Rhino document.
    /// </summary>
    public class ImportIgzCommand : Command
    {
        public override string EnglishName => "ImportIgz";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // File dialog
            var fd = new Rhino.UI.OpenFileDialog
            {
                Filter = "IngeTrazo Files (*.igz)|*.igz|All Files (*.*)|*.*",
                Title = "Import IngeTrazo .igz File"
            };

            if (!fd.ShowOpenDialog())
                return Result.Cancel;

            string filePath = fd.FileName;
            if (string.IsNullOrEmpty(filePath))
                return Result.Cancel;

            // Options
            var options = new IgzImportOptions();

            if (mode == RunMode.Interactive)
            {
                // Quick options via command line
                var goImport = new GetOption();
                goImport.SetCommandPrompt("IGZ import options (press Enter to accept defaults)");
                var optHidden = new OptionToggle(options.ImportHidden, "No", "Yes");
                var optGroups = new OptionToggle(options.CreateRhinoGroups, "No", "Yes");
                var optEdges = new OptionToggle(options.ImportEdges, "No", "Yes");
                goImport.AddOptionToggle("ImportHidden", ref optHidden);
                goImport.AddOptionToggle("CreateGroups", ref optGroups);
                goImport.AddOptionToggle("ImportEdges", ref optEdges);
                goImport.AcceptNothing(true);

                while (true)
                {
                    var rc = goImport.Get();
                    if (rc == GetResult.Nothing || rc == GetResult.Cancel)
                        break;
                }

                options.ImportHidden = optHidden.CurrentValue;
                options.CreateRhinoGroups = optGroups.CurrentValue;
                options.ImportEdges = optEdges.CurrentValue;
            }

            try
            {
                RhinoApp.WriteLine($"Loading {System.IO.Path.GetFileName(filePath)}...");

                var igzDoc = IgzDocument.Load(filePath);

                RhinoApp.WriteLine($"  Format: v{igzDoc.FormatVersion}, App: {igzDoc.AppVersion}");
                if (igzDoc.EmbeddedTextures.Count > 0)
                    RhinoApp.WriteLine($"  Embedded textures: {igzDoc.EmbeddedTextures.Count}");

                var builder = new IgzSceneBuilder(doc, igzDoc, options);
                int count = builder.Build();

                RhinoApp.WriteLine($"Import complete: {count} object(s) created.");

                // Zoom extents
                doc.Views.ActiveView?.ActiveViewport.ZoomExtents();
                doc.Views.Redraw();

                return Result.Success;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error importing .igz file: {ex.Message}");
                RhinoApp.WriteLine(ex.StackTrace);
                return Result.Failure;
            }
        }
    }
}
