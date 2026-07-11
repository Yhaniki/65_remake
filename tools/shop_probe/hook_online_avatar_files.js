/*
 * Shop-avatar resource probe for the ONLINE client (sdo.bin).
 *
 * The online client PACKS avatars into Datas\Avatar.bin, so hooking the Windows file-open
 * APIs alone only shows "Avatar.bin" being opened once — NOT the individual skeleton /
 * motion / mesh. So the PRIMARY hook here is the online "load resource BY NAME" function,
 * whose address we know from the online decompile H:\sdo_cn\sdo.bin.c (PE32 imageBase
 * 0x400000, no ASLR → FUN_00xxxxxx == runtime VA, same convention the ShowTime hook used):
 *
 *   FUN_00438f70(this=ECX, char* name)   __thiscall — args[0] = the resource name
 *      called with "wshop0001.hrc", "mshop0001.hrc", "101013_hua10.hrc", item meshes, etc.
 *      (verified sdo.bin.c:54681 def, :512956/:560599 shop-init call sites)
 *
 * Hooking it logs EVERY resource the shop loads by name — from inside the packed Avatar.bin —
 * so you can see which skeleton (.HRC) and whether ANY motion (.MOT) is loaded for the shop
 * preview (bind-pose mannequin vs wrest/mrest idle), plus the .MSH per card.
 *
 * A SECONDARY set of file-open API hooks catches anything read as a loose file (Avatar.bin
 * itself, or unpacked assets). Frida 17 compatible. Logs to console AND a text file.
 */

var LOG = 'H:\\65_remake\\tools\\shop_probe\\shop_avatar_online_log.txt';
var IMAGE_BASE = 0x400000;
var MODULE = 'sdo.bin';
var VA_LOAD_BY_NAME = 0x00438f70;   // FUN_00438f70 — load resource by name (name = args[0])

var file = null;
try { file = new File(LOG, 'w'); } catch (e) { }
var t0 = Date.now();
function log(s) {
  var line = '[' + ((Date.now() - t0) / 1000).toFixed(2) + 's] ' + s;
  console.log(line);
  if (file) { try { file.write(line + '\n'); file.flush(); } catch (e) { } }
}

// Resolve the runtime module base (no-ASLR build → 0x400000, but read it to be safe).
var modBase = ptr(IMAGE_BASE);
try {
  var m = Process.findModuleByName(MODULE);
  if (m) { modBase = m.base; log('module ' + MODULE + ' base=' + modBase); }
  else log('module ' + MODULE + ' not found by name — assuming base 0x400000');
} catch (e) { }
function A(va) { return modBase.add(va - IMAGE_BASE); }

// Only surface avatar-relevant names (skip the flood of textures/sounds/ui).
var RX = /\.hrc|\.mot|\.msh|face_huan|lingdang|chibang|\bwshop|\bmshop|wrest|mrest|female\.hrc|male\.hrc|\.pak|\.sai|avatar\.bin/i;
var seen = {};
function note(tag, name) {
  if (!name) return;
  if (!RX.test(name)) return;
  if (seen[name] && (Date.now() - seen[name]) < 400) return;   // collapse rapid repeats
  seen[name] = Date.now();
  log(tag + '  ' + name);
}

// Read a char* that might be at args[0] (stack) for a __thiscall whose ECX is `this`.
function readName(args, ctx) {
  // Primary: first stack arg = the name (Frida args[0] = [esp+4] on x86).
  try { var s = args[0].readAnsiString(); if (s && s.length > 1) return s; } catch (e) { }
  // Fallback: some call shapes pass the name via ECX.
  try { var s2 = ctx.ecx ? ptr(ctx.ecx).readAnsiString() : null; if (s2 && s2.length > 1) return s2; } catch (e) { }
  return null;
}

// ---- PRIMARY: online load-resource-by-name ----
try {
  Interceptor.attach(A(VA_LOAD_BY_NAME), {
    onEnter: function (args) { note('RES', readName(args, this.context)); }
  });
  log('hooked FUN_00438f70 (load-by-name) @ ' + A(VA_LOAD_BY_NAME));
} catch (e) { log('!! failed to hook load-by-name: ' + e); }

// ---- SECONDARY: Windows file-open APIs (Avatar.bin / any loose files) ----
var MODS = ['kernel32.dll', 'kernelbase.dll', 'msvcrt.dll', 'ucrtbase.dll'];
function resolve(name) {
  for (var i = 0; i < MODS.length; i++) {
    try { var m = Process.findModuleByName(MODS[i]); if (m) { var e = m.findExportByName(name); if (e) return e; } } catch (e) { }
  }
  try { if (typeof Module.findExportByName === 'function') { var g = Module.findExportByName(null, name); if (g) return g; } } catch (e) { }
  return null;
}
function hookFile(name, wide) {
  var p = resolve(name); if (!p) return;
  Interceptor.attach(p, { onEnter: function (args) { try { note('FILE', wide ? args[0].readUtf16String() : args[0].readAnsiString()); } catch (e) { } } });
}
['CreateFileW', 'CreateFileMappingW', '_wfopen'].forEach(function (n) { hookFile(n, true); });
['CreateFileA', 'CreateFileMappingA', 'fopen'].forEach(function (n) { hookFile(n, false); });

log('probe ready. In the game: open 商城 → 服装店/饰品店, click 发型/表情/项链/下装 tabs, hover + try-on.');
log('KEY: watch for wshop000#.hrc / mshop000#.hrc (mannequin). If a wrest/mrest .MOT ALSO loads');
log('     → the shop applies that idle motion. If NOT → the shop shows the HRC bind pose.  Log: ' + LOG);
