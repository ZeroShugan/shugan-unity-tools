# 🛠️ Shugan Unity Tools

Free Unity editor tools for VRChat avatar creators, made by **Shugan**.
Everything opens from the Unity menu **`Tools > Shugan`**.

<img width="1400" height="855" alt="Shugan Unity Tools" src="https://github.com/user-attachments/assets/d002c9c7-1ae7-4085-833a-3907b1ac2146" />

---

## 💾 Installation

Install with the **VRChat Creator Companion (VCC)**:

1. Open **[tools.shugan.dev](https://tools.shugan.dev/)** and click **Add to VCC**.
   *(Or in VCC go to **Settings → Packages → Add Repository** and paste `https://tools.shugan.dev/index.json`.)*
2. In VCC, open your project's **Manage Project** page and add **Shugan Unity Tools**.
3. Open Unity. The tools are under **`Tools > Shugan`**.

![Adding the repository in VCC](https://github.com/user-attachments/assets/37f77af0-af4d-4362-a058-47d129048bb8)

Updates show up in VCC like any other package. If you don't use VCC, a `.unitypackage` of each version is on the
[Releases](https://github.com/ZeroShugan/shugan-unity-tools/releases) page.

**Requirements:** Unity 2022.3 with the VRChat Avatars SDK.

---

## 🧰 The tools

| Tool | What it does |
|---|---|
| **AutoRig Feet** | Adds toe bones and their weights to your avatar's feet in one click, then sets up the feet menu toggles. Needs Blender and the AutoRig Feet bundle (sold separately, see below). |
| **Blender Bridge** | Sends an avatar's model to Blender, runs a script on it, and brings the result back into Unity. Used by AutoRig Feet. |
| **Humanoid Rig Mapping** | Sets a model to Humanoid and fixes the bones Unity maps wrong, like the feet, toes and jaw. |
| **FBX Swapper** | Makes a new prefab that uses an updated FBX, keeping your materials, blend shapes and settings. The original prefab and FBX are never changed. |
| **Add Prefabs** | Adds many prefabs under an object at once, from a folder or a hand-picked list. |
| **PhysBone & Constraint Helper** | Fills in the missing bone references on PhysBones, colliders, contacts and constraints, for example after an FBX swap. |
| **Animation Clip Batch Editor** | Edits many animation clips at once: change values, rename the animated paths (for example left to right), or duplicate clips and animator layers. |
| **Displace Component** | A component that moves other components onto another object, or onto a humanoid bone, when the avatar is built. Add it with **Add Component → Displace Component**. |
| **Package Exporter** | Exports your files as a `.unitypackage` from saved presets, with automatic version numbers. |

Need a hand? Ask on [Discord](https://discord.com/invite/6FZmzkb).

---

## 🦶 Feet & Toes Gesture packages

These tools go together with Shugan's **Feet/Toes Gesture** products: animated feet and toes you control with hand
gestures. The **AutoRig Feet** bundle (the Blender script and the gesture prefabs) is sold on the stores below; the
Unity tools on this page are free.

- 🛒 [Booth](https://shugan.booth.pm/)
- 🛒 [Gumroad](https://gumroad.com/shugan)
- 🛒 [Blender Market](https://blendermarket.com/creators/shugan)

---

## 📄 License

Copyright © 2026 Shugan. All rights reserved.

You may use these tools for free on your own avatars, including avatars you sell or make on commission, and change
your own copy for your personal use. You may not redistribute, re-upload or sell them (changed or not), present them
as your own work, or use them to train AI models or have an AI tool reproduce them.
Full terms: [LICENSE.md](Packages/com.zeroshugan.shugan-unity-tools/LICENSE.md).

---

**Made by Shugan** · [Discord](https://discord.com/invite/6FZmzkb) · [tools.shugan.dev](https://tools.shugan.dev/)
