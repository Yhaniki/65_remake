#!/usr/bin/env python3
"""Generate SceneMapobjCatalog.cs from the reverse-engineered per-scene mapobj table.

Source : docs/reverse-engineering/SDO_SCENE_MAPOBJ_TABLE.json  (decoded from
         Scene_LoadBackground / FUN_004b43c0 in sdo_stand_alone.exe).
Disk   : assets/sdox_offline/Extracted/SCENE/MAPOBJ/  (the unpacked .bin archives).
Output : 65/My project/Assets/Scripts/Game/SceneMapobjCatalog.cs  (pure C#, no UnityEngine).

The decompiled table names each mapobj by its archive name (e.g. "guatan", "fenmu_dong1")
and gives the real mesh file name in `msh` (e.g. "guatan.msh", "guang4.msh"). The extracted
tree, however, is irregular: clean scenes keep one top-level folder per prop
(GUATAN/GUATAN.MSH, BEACH/LANG.MSH), while themed scenes nest every prop under a scene
parent with arbitrary sub-paths and renamed files (14_HAIDI/GUANG/GUANG.MSH,
FENMU/DONG1/GUANG4.MSH, 19_SUBWAY/VT1/TV1.MSH, ROOM_NIGHT_OBJ/LANG/ROOM_SCENE.MSH).

So we RESOLVE each prop against the real files on disk: index every .MSH by base name,
detect each scene's parent folder (the top-level dir that covers >=2 of its props), then
pick the candidate under that parent (nested scenes) or the candidate whose top-level
folder equals the prop name (clean scenes). The catalog stores the resolved relative
sub-folder as `Folder`, so Step1Game's loader (which just joins folder + file) reaches the
right .MSH/.HRC/.MOT untouched — for every scene, nested ones included.

Only 3D mapobjs (msh/hrc/mot geometry) are emitted; 2D billboards / lights / particle
effects are not.

Run:  python tools/build_scene_mapobj_catalog.py
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = os.path.join(ROOT, "docs", "reverse-engineering", "SDO_SCENE_MAPOBJ_TABLE.json")
MAPOBJ_ROOT = os.path.join(ROOT, "assets", "sdox_offline", "Extracted", "SCENE", "MAPOBJ")
OUT = os.path.join(ROOT, "65", "My project", "Assets", "Scripts", "Game", "SceneMapobjCatalog.cs")


def fnum(v):
    """Round-trip float -> C# float literal (shortest form, with 'f' suffix)."""
    f = float(v)
    s = repr(f)
    if s.endswith(".0"):
        s = s[:-2]
    return s + "f"


def base_noext(name):
    return os.path.splitext(os.path.basename(name.replace("\\", "/")))[0]


def cs_string(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


# ---- disk index -------------------------------------------------------------

def index_disk():
    """base-name(lower) -> [relative .MSH paths under MAPOBJ_ROOT] (forward slashes)."""
    idx = {}
    for dirpath, _, files in os.walk(MAPOBJ_ROOT):
        for f in files:
            if f.lower().endswith(".msh"):
                rel = os.path.relpath(os.path.join(dirpath, f), MAPOBJ_ROOT).replace("\\", "/")
                idx.setdefault(os.path.splitext(f)[0].lower(), []).append(rel)
    return idx


def top_folder(rel):
    return rel.split("/")[0]


def sub_dir(rel):
    return "/".join(rel.split("/")[:-1])   # the folder holding the .MSH, relative to MAPOBJ_ROOT


def real_sibling(rel, base, ext):
    """Find a sibling file <base>.<ext> next to the resolved .MSH; return its real name or None."""
    folder = os.path.join(MAPOBJ_ROOT, os.path.dirname(rel))
    want = (base + "." + ext).lower()
    try:
        for f in os.listdir(folder):
            if f.lower() == want:
                return f
    except OSError:
        pass
    return None


def detect_parent(mapobjs, idx):
    """The scene's parent top-level folder = the one whose subtree holds >=2 of its props."""
    cov = {}
    for m in mapobjs:
        tops = set(top_folder(p) for p in idx.get(base_noext(m["msh"]).lower(), []))
        for t in tops:
            cov[t] = cov.get(t, 0) + 1
    if not cov:
        return None
    best = max(cov, key=lambda k: cov[k])
    return best if cov[best] >= 2 else None


def resolve(m, parent, idx):
    """Resolve a prop to its on-disk relative .MSH path (or None if absent)."""
    name = m["name"]
    cands = idx.get(base_noext(m["msh"]).lower(), [])
    if not cands:
        return None
    # nested scene: prefer the candidate that lives under the detected parent folder
    if parent:
        under = [p for p in cands if top_folder(p) == parent]
        if under:
            return min(under, key=len)
    # clean scene: the prop's own top-level folder equals its name (GUATAN, FIFA_GUANGGAO, ...)
    byname = [p for p in cands if top_folder(p).lower() == name.lower()]
    if byname:
        return min(byname, key=len)
    if len(cands) == 1:
        return cands[0]
    return min(cands, key=len)   # ambiguous -> shortest path, reported by caller


# ---- build ------------------------------------------------------------------

def build():
    with open(SRC, encoding="utf-8") as fh:
        data = json.load(fh)
    idx = index_disk()

    scenes_out = []   # (folder_key, [ (subdir, msh, hrc, mot, [ (x,y,z,s) ]) ])
    unresolved = []
    for scene in data["scenes"]:
        folder = scene.get("folder")
        mapobjs = scene.get("mapobjs", [])
        if not folder or not mapobjs:
            continue
        parent = detect_parent(mapobjs, idx)
        groups = []
        for m in mapobjs:
            name = m["name"]
            rel = resolve(m, parent, idx)
            if rel:
                subdir = sub_dir(rel)
                msh = os.path.basename(rel)
                hbase = base_noext(m.get("hrc") or m.get("msh"))
                hrc = real_sibling(rel, hbase, "hrc") or (hbase.upper() + ".HRC")
                mot = None
                if m.get("mot"):
                    mot = real_sibling(rel, base_noext(m["mot"]), "mot")
            else:
                # not on disk: keep the table's intent (folder=name, file=msh) so the data
                # survives; the runtime loader logs it missing and skips it.
                subdir = name.upper()
                msh = base_noext(m["msh"]).upper() + ".MSH"
                hrc = base_noext(m.get("hrc") or m.get("msh")).upper() + ".HRC"
                mot = base_noext(m["mot"]).upper() + ".MOT" if m.get("mot") else None
                unresolved.append(f"{folder}/{name} ({m.get('msh')})")
            insts = []
            for inst in m.get("instances", []):
                pos = inst["pos"]
                scale = inst.get("scale", [1, 1, 1])
                s0 = scale[0] if isinstance(scale, (list, tuple)) else scale
                if isinstance(scale, (list, tuple)) and not all(abs(s - s0) < 1e-6 for s in scale):
                    print(f"  WARN non-uniform scale {scale} on {folder}/{name}; using {s0}")
                insts.append((pos[0], pos[1], pos[2], s0))
            groups.append((subdir, msh, hrc, mot, insts))
        scenes_out.append((folder.upper(), groups))
    return scenes_out, unresolved


def emit(scenes_out):
    lines = []
    w = lines.append
    w("// <auto-generated>")
    w("//   Generated by tools/build_scene_mapobj_catalog.py from")
    w("//   docs/reverse-engineering/SDO_SCENE_MAPOBJ_TABLE.json, resolved against the")
    w("//   extracted SCENE/MAPOBJ tree on disk. DO NOT EDIT BY HAND.")
    w("// </auto-generated>")
    w("using System.Collections.Generic;")
    w("")
    w("namespace Sdo.Game")
    w("{")
    w("    /// <summary>One placed instance of a mapobj (native SDO world coords, uniform scale).</summary>")
    w("    public struct MapobjPlacement")
    w("    {")
    w("        public readonly float X, Y, Z, Scale;")
    w("        public MapobjPlacement(float x, float y, float z, float scale) { X = x; Y = y; Z = z; Scale = scale; }")
    w("    }")
    w("")
    w("    /// <summary>A mapobj prop group: one mesh/skeleton/motion loaded once, placed at N transforms.</summary>")
    w("    public sealed class MapobjGroup")
    w("    {")
    w("        public readonly string Folder;            // SCENE/MAPOBJ sub-folder of the .MSH (may be nested, e.g. \"14_HAIDI/GUANG\")")
    w("        public readonly string Msh, Hrc, Mot;     // file names within Folder (with extension); Mot may be null")
    w("        public readonly MapobjPlacement[] Instances;")
    w("        public MapobjGroup(string folder, string msh, string hrc, string mot, MapobjPlacement[] instances)")
    w("        { Folder = folder; Msh = msh; Hrc = hrc; Mot = mot; Instances = instances; }")
    w("    }")
    w("")
    w("    /// <summary>")
    w("    /// Per-scene 3D stage props, generated from the decompiled Scene_LoadBackground table and resolved")
    w("    /// against the real extracted file tree. Keyed by the scene FOLDER name (last segment of")
    w("    /// Step1Game.scenePath, e.g. \"SCN0009\"). Scenes with no mapobj — or unknown folders — return an empty")
    w("    /// list. 2D billboards / lights / particle effects are not included.")
    w("    /// </summary>")
    w("    public static class SceneMapobjCatalog")
    w("    {")
    w("        private static readonly IReadOnlyList<MapobjGroup> Empty = new MapobjGroup[0];")
    w("        private static readonly Dictionary<string, MapobjGroup[]> ByFolder = Build();")
    w("")
    w("        /// <summary>Mapobjs for a scene folder (e.g. \"SCN0009\"); empty list if none / unknown.</summary>")
    w("        public static IReadOnlyList<MapobjGroup> ForFolder(string folder)")
    w("        {")
    w("            if (string.IsNullOrEmpty(folder)) return Empty;")
    w("            return ByFolder.TryGetValue(folder.ToUpperInvariant(), out var g) ? g : Empty;")
    w("        }")
    w("")
    w("        private static Dictionary<string, MapobjGroup[]> Build()")
    w("        {")
    w("            return new Dictionary<string, MapobjGroup[]>")
    w("            {")
    for folder, groups in scenes_out:
        w(f"                [{cs_string(folder)}] = new[]")
        w("                {")
        for subdir, msh, hrc, mot, insts in groups:
            mot_lit = cs_string(mot) if mot else "null"
            w(f"                    new MapobjGroup({cs_string(subdir)}, {cs_string(msh)}, {cs_string(hrc)}, {mot_lit}, new[]")
            w("                    {")
            per = 3
            for i in range(0, len(insts), per):
                cells = ", ".join(
                    f"new MapobjPlacement({fnum(x)}, {fnum(y)}, {fnum(z)}, {fnum(s)})"
                    for (x, y, z, s) in insts[i:i + per]
                )
                w(f"                        {cells},")
            w("                    }),")
        w("                },")
    w("            };")
    w("        }")
    w("    }")
    w("}")
    return "\n".join(lines) + "\n"


def main():
    scenes_out, unresolved = build()
    text = emit(scenes_out)
    with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    total_groups = sum(len(g) for _, g in scenes_out)
    total_inst = sum(len(i) for _, gs in scenes_out for (_, _, _, _, i) in gs)
    print(f"wrote {OUT}")
    print(f"  {len(scenes_out)} scenes, {total_groups} mapobj groups, {total_inst} instances")
    if unresolved:
        print(f"  {len(unresolved)} props not found on disk (emitted as best-guess, runtime will skip):")
        for u in unresolved:
            print("    -", u)
    else:
        print("  all props resolved to real files on disk")


if __name__ == "__main__":
    main()
