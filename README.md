# 🛠️ Shugan Unity Tools

Three Unity Editor tools for avatar customization:

1. **FBX Swapper** - Creates new prefab variants with swapped FBX models while keeping all materials, blendshapes, and settings
2. **Add Prefabs** - Adds multiple prefabs to a scene object at once
3. **PhysBone & Constraint Helper** - Auto-fills transform references in VRChat PhysBone and Constraint components

<img width="1400" height="855" alt="image" src="https://github.com/user-attachments/assets/d002c9c7-1ae7-4085-833a-3907b1ac2146" />

---

## 📑 Table of Contents

- [Tools Overview](#-tools-overview)
  - [FBX Swapper](#1-fbx-swapper)
  - [Add Prefabs](#2-add-prefabs)
  - [PhysBone & Constraint Helper](#3-physbone--constraint-helper)
- [Feet/Toes Gesture Packages](#-feettoes-gesture-packages)
- [Usage Guides](#-usage-guides)
  - [FBX Swapper Usage](#fbx-swapper-usage)
  - [Add Prefabs Usage](#add-prefabs-usage)
  - [PhysBone & Constraint Helper Usage](#physbone--constraint-helper-usage)
- [Installation](#-installation)
- [Requirements](#-requirements)
- [Support & Links](#-support--links)

---

## 🔍 Tools Overview

### 1. FBX Swapper
**Location:** `Tools > Shugan > FBX Swapper`

Creates new prefab variants with swapped FBX models while preserving all customizations.

**✨ Key Features:**
- Creates new prefabs with updated FBX models
- Preserves materials, blendshape values, and animation settings
- Maintains bone constraints and hierarchy
- Works with both prefabs and scene objects
- Never modifies original prefabs or FBX files

**💡 Use Cases:**
- Create new prefab with an updated FBX version
- Update a prefab with new FBX without replacing the original FBX file in Unity

---

### 2. Add Prefabs
**Location:** `Tools > Shugan > Add Prefabs`

Quickly adds multiple prefabs as children to a selected scene object.

**✨ Key Features:**
- Batch add prefabs from folders
- Manual prefab selection option
- Auto-detects selected objects in the scene
- Select/deselect all functionality
- Preserves scene hierarchy

**💡 Use Cases:**
- Adding multiple accessories to an avatar
- Quickly populating a scene object with children
- Batch importing prefab collections

---

### 3. PhysBone & Constraint Helper
**Location:** `Tools > Shugan > PhysBone & Constraint Helper`

Automatically fills transform references in VRChat PhysBone and Constraint components by finding matching objects in the hierarchy.

**✨ Key Features:**
- Auto-fills `rootTransform` for VRC PhysBone components
- Auto-fills `rootTransform` for VRC PhysBone Collider components
- Auto-fills `rootTransform` for VRC Contact Sender/Receiver components
- Auto-fills `TargetTransform` and `SourceTransform` for VRC Constraints
- Handles ambiguous matches with manual selection
- Supports custom search root for precise matching
- Analyzes entire selection including children

**💡 Use Cases:**
- Setting up PhysBones after FBX swapping
- Fixing broken references after model updates
- Batch configuration of VRChat components

---

## 🦶 Feet/Toes Gesture Packages

These tools are designed to work seamlessly with Shugan's **Feet/Toes Gesture** packages - avatar-specific products that add controllable foot and toe animations using wrist and finger gestures.

**FBX Swapper** includes preset configurations for supported avatars:
- **MANUKA** - Auto-detects prefab variants and FBX files
- **RINDO** - Pre-configured paths for quick setup
- More avatars added with each package release

When you purchase a Feet/Toes Gesture package for a specific avatar, the preset handles all the technical setup automatically, making installation quick and error-free.

---

## 📖 Usage Guides

### FBX Swapper Usage

#### 1. Choose Model Preset
- **Model Type**: Select a preset (MANUKA, RINDO) for automatic configuration, or "Custom" for manual setup
- **Prefab Variant**: (Only appears for presets with multiple variants) Choose which prefab variant to use (e.g., MANUKA Poiyomi vs MANUKA lilToon)

#### 2. Target Prefab/Scene Object
- **Target (Prefab/Scene Obj)**: Drag your prefab or scene object here
  - Use a prefab to modify an existing avatar
  - Use a scene object to convert your customized avatar to a new prefab

#### 3. FBX Models
- **New FBX Model**: The updated FBX file you want to use (e.g., model with added toes/feet bones)
- **Old FBX to Replace**: The original FBX file currently in your prefab that will be replaced

#### 4. Generation Options
- **Add to Scene After Creation**: Check this to automatically add the new prefab to your scene for immediate preview

#### 5. Generate New Prefab
- Click **"Generate FBX-Swapped Prefab"** to create the new prefab with swapped FBX
- The new prefab is created in the same folder as your target prefab

**Quick Steps:**
1. Open `Tools > Shugan > FBX Swapper`
2. Select a model preset or choose "Custom"
3. Assign your target prefab or scene object
4. Select the new FBX model and old FBX to replace
5. Click "Generate FBX-Swapped Prefab"
6. The new prefab will be created in the same folder as your original

---

### Add Prefabs Usage

#### 1. Target Scene Object
- **Auto-Detect Selection**: When checked, automatically uses your currently selected scene object
- **Target Object**: The scene object that will become the parent of all added prefabs
- Click **"Refresh"** to update the target from your current selection

#### 2. Select Model Folder
- **Prefabs Base Path**: The folder path where your model folders are located (e.g., `Assets/! Shugan/!_Prefabs/Custom`)
  - Click **"Browse"** to select a different base folder
  - Click **"Refresh"** to reload available folders
- **Model Folder**: Choose which avatar/model folder to load prefabs from (e.g., MANUKA_v1_02)
- **Prefabs in [folder name]**: List of all prefabs in the selected folder
  - Check/uncheck individual prefabs to select which ones to add
  - Click **"Select All"** or **"Deselect All"** for quick selection

#### 3. Manual Prefab Selection (Optional)
- **Manual Prefabs**: Add specific prefabs that aren't in your folder system
  - Click **"+ Add Prefab Slot"** to add more slots
  - Drag individual prefabs into these slots
  - These will be added alongside any folder prefabs you selected

#### 4. Add Prefabs to Scene
- Shows summary: number of prefabs ready to add
- Click **"Add Prefab(s) to Scene"** to add all selected prefabs as children of your target object

**Quick Steps:**
1. Open `Tools > Shugan > Add Prefabs`
2. Select a target object in your scene
3. Choose prefabs from the folder browser or add them manually
4. Click "Add Prefabs to Scene"

---

### PhysBone & Constraint Helper Usage

#### 1. Selected Objects Analysis
- Click **"Analyze Selection"** to scan your selected objects for VRC components
- Click **"Clear"** to reset the analysis
- Shows counts of objects with:
  - VRC PhysBone
  - VRC PhysBone Collider
  - VRC Contact Sender
  - VRC Contact Receiver
  - VRC Constraints
- Note: Children of selected objects are automatically included

#### 2. Search Root Configuration
- **Common Root**: Auto-detected parent object that contains all your selected objects
- **Custom Search Root (Optional)**: Manually specify a different root to search under
  - Leave empty to use the auto-detected common root
  - Click **"Refresh Matches with Custom Root"** after setting
  - Click **"Clear Custom Root"** to go back to auto-detection

#### 3. Matching Objects
- Shows all components that need transform references filled
- For each component, displays:
  - **✓ 1 match**: Perfect - will auto-fill automatically
  - **⚠ Multiple matches**: You must select the correct one from the dropdown
  - **⚠ No match**: No matching object found under the search root
- **Manual Selection Dropdown**: Choose the correct match when multiple options exist
- Click **"Auto-Fill Transform Fields"** to apply all matches

**Quick Steps:**
1. Open `Tools > Shugan > PhysBone & Constraint Helper`
2. Select objects in the hierarchy that have VRC components
3. Click "Analyze Selection"
4. Review the matching objects found
5. For ambiguous matches, select the correct match from the dropdown
6. Click "Auto-Fill Transform Fields"

---

## 💾 Installation

### Method 1: VCC (VRChat Creator Companion) - Recommended

1. Copy this link: `https://zeroshugan.github.io/vcc-listing/index.json`
2. Open VRChat Creator Companion (VCC)
3. Go to **Settings** → **Packages** → **Add Repository**
4. Paste the link and click **Add**
5. The tools will now be available in your VCC package list
6. Add them to your project through VCC
![CreatorCompanion_M3XtIuGtXT](https://github.com/user-attachments/assets/37f77af0-af4d-4362-a058-47d129048bb8)

### Method 2: Manual Installation

1. Download the latest release
2. Extract the files to your Unity project's `Assets` folder
3. The tools will appear under `Tools > Shugan` in the Unity Editor menu

---

## ⚙️ Requirements

- Unity Editor (tested with Unity 2019.4 and later)
- For PhysBone Helper: VRChat SDK3 (Avatars 3.0)

---

## 🔗 Support & Links

### Need Help?
- 💬 **Discord:** [Join our Discord](https://discord.com/invite/6FZmzkb)
- 📚 **Wiki:** [Documentation](https://www.notion.so/shugan/FBX-Swapper-253d98525501802ca7c9e7eb7738e0ec)

### Store Links
- 🛒 **Booth:** [shugan.booth.pm](https://shugan.booth.pm/)
- 🛒 **Gumroad:** [gumroad.com/shugan](https://gumroad.com/shugan)
- 🛒 **Blender Market:** [blendermarket.com/creators/shugan](https://blendermarket.com/creators/shugan)

---

## 📄 License

These tools are provided as-is for use with Shugan products and general Unity workflows. Redistribution and modification are permitted for personal use.

---

## 👤 Credits

**Created by Shugan**

*For detailed usage instructions and troubleshooting, please visit our [Wiki](https://www.notion.so/shugan/FBX-Swapper-253d98525501802ca7c9e7eb7738e0ec) or join our [Discord community](https://discord.com/invite/6FZmzkb).*
