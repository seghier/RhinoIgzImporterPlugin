# RhinoIgzImporterPlugin

A Rhino 8 plugin for importing **IngeTrazo** (`.igz`) 3D documents with full support for geometry, layers, materials, textures, component hierarchies, and linear dimensions.

---

## 📌 Acknowledgements & Attribution

This importer is designed for models created with **[IngeTrazo](https://github.com/ingelibre/ingetrazo)**.
- **Original Repository:** [https://github.com/ingelibre/ingetrazo](https://github.com/ingelibre/ingetrazo)
- **Author / Organization:** Marco Sumari Tellez & IngeLibre contributors
- **Format:** IngeTrazo Document format (`.igz`, Format 1 JSON and Format 2 ZIP container)

---

## 🚀 Features

- **Format 1 & Format 2 Support:** Automatically handles both plain JSON `.igz` files and ZIP archive `.igz` packages containing embedded textures.
- **Material & Texture Extraction:**
  - Extracts embedded PNG/JPEG textures automatically to local application storage.
  - Registers both native `Rhino.DocObjects.Material` and modern Rhino 8 `Rhino.Render.RenderMaterial` so they appear directly in the Rhino **Materials** panel.
  - Generates representative diffuse colors so shaded views look natural even before switching to Rendered mode.
- **Accurate UV Mapping:**
  - Implements IngeTrazo's exact planar projection basis calculation (compatible with SketchUp `.skp` mapping conventions).
  - Preserves real-world texture tiling dimensions (`sw`, `sh`) and in-plane rotation (`rot`).
  - Supports 2D billboard components (e.g. cutouts/people) with proper UV coordinates and alpha transparency.
- **Mesh & Face Organization:**
  - Groups geometry cleanly by `(Layer, Material)` so distinct materials are applied to separate Rhino mesh objects rather than merged indiscriminately.
- **Full Layer Support:**
  - Recreates document layers with their original names, display colors, visibility flags, and lock states.
- **Linear Dimensions & Annotations:**
  - Converts IGZ dimension annotations into native Rhino `LinearDimension` objects with proper alignment and offsets.
  - Adds visual dimension and witness/extension lines for clear viewing across Wireframe, Shaded, and Rendered viewports.
- **Component & Group Hierarchy:**
  - Handles groups, nested sub-groups, and component prototypes with 4×4 column-major transforms.
  - Translates IGZ groups into native Rhino object groups.
- **Unit Conversion:**
  - Reads model unit settings (metres, millimetres, inches, etc.) and scales coordinates seamlessly to the active Rhino document units.

---

## 💻 Requirements

- **Rhino 8** (Windows)
- **.NET 8.0** runtime (included with Rhino 8)

---

## 📥 Installation

1. Download or build `RhinoIgzImporter.rhp`.
2. Launch Rhino 8.
3. Drag and drop `RhinoIgzImporter.rhp` into any active Rhino viewport, or go to:
   - **Tools** → **Options** → **Plug-ins** → **Install...** and select `RhinoIgzImporter.rhp`.

---

## 🛠️ Usage

### Option 1: Rhino Command
Type the command in the Rhino command line:
```text
ImportIgz
```
An open file dialog will appear allowing you to select your `.igz` file, followed by optional command-line switches (`ImportHidden`, `CreateGroups`, `ImportEdges`).

### Option 2: File → Import / Open
Go to **File** → **Import...** (or **File** → **Open...**) and select **IngeTrazo Document (*.igz)** from the file type dropdown.

### Option 3: Drag and Drop
Simply drag any `.igz` file from Windows Explorer directly into the Rhino viewport.

---

## 🏗️ Building from Source

To compile the plugin from source:

```powershell
dotnet build "RhinoIgzImporter.csproj" -c Release
```

The compiled `.rhp` assembly will be output to:
`bin\Release\net8.0-windows\RhinoIgzImporter.rhp`

---

## 📄 License

GPL-3.0 / Compatible Open Source license.
