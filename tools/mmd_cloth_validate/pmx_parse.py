# -*- coding: utf-8 -*-
"""Minimal PMX 2.0 parser for the cloth-physics ground-truth sim.

Parses ONLY what the reference simulation needs:
  - header globals (encoding + index sizes)
  - vertices (positions only, for model height -> unitsPerMeter; weights skipped)
  - faces / textures / materials / morphs / display frames (skipped byte-exact)
  - bones (name / position / parent  -> chain geometry + FK anchor driving)
  - rigid bodies (full record)
  - joints (full record, PMX 2.0 spring-6DOF layout)

Format reference: PmxLoader.cs in the 65_remake-mmd Unity worktree (validated
against this exact model) — the byte layout here mirrors it 1:1.
"""
import struct


class Bone:
    __slots__ = ("name_jp", "name_en", "position", "parent")

    def __init__(self):
        self.name_jp = ""
        self.name_en = ""
        self.position = (0.0, 0.0, 0.0)
        self.parent = -1


class RigidBody:
    __slots__ = ("name_jp", "name_en", "bone", "group", "mask", "shape", "size",
                 "position", "rotation", "mass", "linear_damp", "angular_damp",
                 "restitution", "friction", "mode", "index")

    def __init__(self):
        self.name_jp = ""
        self.name_en = ""
        self.bone = -1
        self.group = 0
        self.mask = 0xFFFF
        self.shape = 0          # 0 sphere, 1 box, 2 capsule
        self.size = (0.0, 0.0, 0.0)
        self.position = (0.0, 0.0, 0.0)   # model space
        self.rotation = (0.0, 0.0, 0.0)   # radians, MMD euler (R = Ry*Rx*Rz)
        self.mass = 0.0
        self.linear_damp = 0.0
        self.angular_damp = 0.0
        self.restitution = 0.0
        self.friction = 0.0
        self.mode = 0           # 0 kinematic-follow-bone, 1 physics, 2 physics+bone-align
        self.index = -1


class Joint:
    __slots__ = ("name_jp", "name_en", "kind", "rb_a", "rb_b", "position", "rotation",
                 "pos_lo", "pos_hi", "rot_lo", "rot_hi", "pos_spring", "rot_spring")

    def __init__(self):
        self.name_jp = ""
        self.name_en = ""
        self.kind = 0
        self.rb_a = -1
        self.rb_b = -1
        self.position = (0.0, 0.0, 0.0)   # model space anchor
        self.rotation = (0.0, 0.0, 0.0)   # radians, MMD euler
        self.pos_lo = (0.0, 0.0, 0.0)
        self.pos_hi = (0.0, 0.0, 0.0)
        self.rot_lo = (0.0, 0.0, 0.0)
        self.rot_hi = (0.0, 0.0, 0.0)
        self.pos_spring = (0.0, 0.0, 0.0)
        self.rot_spring = (0.0, 0.0, 0.0)


class Pmx:
    def __init__(self, data):
        self._d = data
        self._pos = 0
        self.name_jp = ""
        self.name_en = ""
        self.bones = []
        self.rigid_bodies = []
        self.joints = []
        self.vert_min_y = 1e30
        self.vert_max_y = -1e30
        self._parse()

    # ---- primitive readers ----
    def _u8(self):
        v = self._d[self._pos]
        self._pos += 1
        return v

    def _u16(self):
        v = self._d[self._pos] | (self._d[self._pos + 1] << 8)
        self._pos += 2
        return v

    def _i32(self):
        v = struct.unpack_from("<i", self._d, self._pos)[0]
        self._pos += 4
        return v

    def _f(self):
        v = struct.unpack_from("<f", self._d, self._pos)[0]
        self._pos += 4
        return v

    def _v2(self):
        v = struct.unpack_from("<2f", self._d, self._pos)
        self._pos += 8
        return v

    def _v3(self):
        v = struct.unpack_from("<3f", self._d, self._pos)
        self._pos += 12
        return v

    def _v4(self):
        v = struct.unpack_from("<4f", self._d, self._pos)
        self._pos += 16
        return v

    def _text(self):
        n = self._i32()
        if n <= 0:
            return ""
        raw = self._d[self._pos:self._pos + n]
        self._pos += n
        if self._enc == 0:
            return raw.decode("utf-16-le", errors="replace")
        return raw.decode("utf-8", errors="replace")

    def _sidx(self, size):
        if size == 1:
            v = self._d[self._pos]
            self._pos += 1
            return v - 256 if v >= 128 else v
        if size == 2:
            v = struct.unpack_from("<h", self._d, self._pos)[0]
            self._pos += 2
            return v
        return self._i32()

    def _uidx(self, size):
        if size == 1:
            return self._u8()
        if size == 2:
            return self._u16()
        return self._i32()

    def _bone_ref(self):
        return self._sidx(self._b_idx)

    def _tex_ref(self):
        return self._sidx(self._t_idx)

    def _vert_ref(self):
        return self._uidx(self._v_idx)

    def _morph_ref(self):
        return self._sidx(self._mf_idx)

    def _mat_ref(self):
        return self._sidx(self._m_idx)

    def _rb_ref(self):
        return self._sidx(self._r_idx)

    # ---- sections ----
    def _parse(self):
        d = self._d
        if d[0:4] != b"PMX ":
            raise ValueError("bad magic")
        self._pos = 4
        self.version = self._f()
        n_glob = self._u8()
        g = list(d[self._pos:self._pos + n_glob]) + [0] * 8
        self._pos += n_glob
        self._enc, self._extra_uv = g[0], g[1]
        self._v_idx, self._t_idx, self._m_idx = g[2], g[3], g[4]
        self._b_idx, self._mf_idx, self._r_idx = g[5], g[6], g[7]

        self.name_jp = self._text()
        self.name_en = self._text()
        self._text()
        self._text()

        self._parse_vertices()
        self._parse_faces()
        self._parse_textures()
        self._parse_materials()
        self._parse_bones()
        self._parse_morphs()
        self._parse_display_frames()
        self._parse_rigid_bodies()
        self._parse_joints()

    def _parse_vertices(self):
        vc = self._i32()
        extra = self._extra_uv * 16
        mn, mx = 1e30, -1e30
        for _ in range(vc):
            y = struct.unpack_from("<f", self._d, self._pos + 4)[0]
            if y < mn:
                mn = y
            if y > mx:
                mx = y
            self._pos += 32 + extra  # pos3 + normal3 + uv2
            deform = self._u8()
            b = self._b_idx
            if deform == 0:
                self._pos += b
            elif deform == 1:
                self._pos += b * 2 + 4
            elif deform in (2, 4):
                self._pos += b * 4 + 16
            elif deform == 3:
                self._pos += b * 2 + 4 + 36
            else:
                raise ValueError("unknown deform %d" % deform)
            self._pos += 4  # edge scale
        self.vert_min_y, self.vert_max_y = mn, mx

    def _parse_faces(self):
        n = self._i32()
        self._pos += n * self._v_idx

    def _parse_textures(self):
        n = self._i32()
        for _ in range(n):
            self._text()

    def _parse_materials(self):
        n = self._i32()
        for _ in range(n):
            self._text()
            self._text()
            self._pos += 4 * 11  # diffuse4 + specular3 + specPow + ambient3
            self._u8()           # draw flags
            self._pos += 4 * 5   # edge color4 + edge size
            self._tex_ref()
            self._tex_ref()
            self._u8()           # sphere mode
            if self._u8() == 0:  # shared-toon flag
                self._tex_ref()
            else:
                self._u8()
            self._text()         # memo
            self._i32()          # surface count

    def _parse_bones(self):
        n = self._i32()
        for _ in range(n):
            b = Bone()
            b.name_jp = self._text()
            b.name_en = self._text()
            b.position = self._v3()
            b.parent = self._bone_ref()
            self._i32()  # layer
            flags = self._u16()
            if flags & 0x0001:
                self._bone_ref()
            else:
                self._v3()
            if flags & 0x0100 or flags & 0x0200:
                self._bone_ref()
                self._f()
            if flags & 0x0400:
                self._v3()
            if flags & 0x0800:
                self._v3()
                self._v3()
            if flags & 0x2000:
                self._i32()
            if flags & 0x0020:
                self._bone_ref()
                self._i32()
                self._f()
                links = self._i32()
                for _ in range(links):
                    self._bone_ref()
                    if self._u8():
                        self._v3()
                        self._v3()
            self.bones.append(b)

    def _parse_morphs(self):
        n = self._i32()
        for _ in range(n):
            self._text()
            self._text()
            self._u8()
            mtype = self._u8()
            oc = self._i32()
            for _ in range(oc):
                if mtype == 0 or mtype == 9:
                    self._morph_ref()
                    self._f()
                elif mtype == 1:
                    self._vert_ref()
                    self._v3()
                elif mtype == 2:
                    self._bone_ref()
                    self._v3()
                    self._v4()
                elif mtype in (3, 4, 5, 6, 7):
                    self._vert_ref()
                    self._v4()
                elif mtype == 8:
                    self._mat_ref()
                    self._u8()
                    self._pos += 4 * 28
                elif mtype == 10:
                    self._rb_ref()
                    self._u8()
                    self._v3()
                    self._v3()
                else:
                    raise ValueError("unknown morph type %d" % mtype)

    def _parse_display_frames(self):
        n = self._i32()
        for _ in range(n):
            self._text()
            self._text()
            self._u8()
            ec = self._i32()
            for _ in range(ec):
                if self._u8() == 0:
                    self._bone_ref()
                else:
                    self._morph_ref()

    def _parse_rigid_bodies(self):
        n = self._i32()
        for i in range(n):
            rb = RigidBody()
            rb.index = i
            rb.name_jp = self._text()
            rb.name_en = self._text()
            rb.bone = self._bone_ref()
            rb.group = self._u8()
            rb.mask = self._u16()
            rb.shape = self._u8()
            rb.size = self._v3()
            rb.position = self._v3()
            rb.rotation = self._v3()
            rb.mass = self._f()
            rb.linear_damp = self._f()
            rb.angular_damp = self._f()
            rb.restitution = self._f()
            rb.friction = self._f()
            rb.mode = self._u8()
            self.rigid_bodies.append(rb)

    def _parse_joints(self):
        n = self._i32()
        for _ in range(n):
            j = Joint()
            j.name_jp = self._text()
            j.name_en = self._text()
            j.kind = self._u8()
            j.rb_a = self._rb_ref()
            j.rb_b = self._rb_ref()
            j.position = self._v3()
            j.rotation = self._v3()
            j.pos_lo = self._v3()
            j.pos_hi = self._v3()
            j.rot_lo = self._v3()
            j.rot_hi = self._v3()
            j.pos_spring = self._v3()
            j.rot_spring = self._v3()
            self.joints.append(j)


def load(path):
    with open(path, "rb") as f:
        return Pmx(f.read())


if __name__ == "__main__":
    import sys
    p = load(sys.argv[1])
    print("model: %s / %s (v%.1f)" % (p.name_jp, p.name_en, p.version))
    print("bones=%d rigid=%d joints=%d  vertY=[%.3f, %.3f]" %
          (len(p.bones), len(p.rigid_bodies), len(p.joints), p.vert_min_y, p.vert_max_y))
    print("--- rigid bodies ---")
    for rb in p.rigid_bodies:
        bn = p.bones[rb.bone].name_jp if 0 <= rb.bone < len(p.bones) else "-"
        print("%3d mode=%d shp=%d grp=%2d mask=%04x m=%7.3f ld=%.2f ad=%.2f fr=%.2f re=%.2f  %-28s bone=%s" %
              (rb.index, rb.mode, rb.shape, rb.group, rb.mask, rb.mass,
               rb.linear_damp, rb.angular_damp, rb.friction, rb.restitution, rb.name_jp, bn))
    print("--- joints ---")
    for j in p.joints:
        print("%-28s A=%3d B=%3d rotLo=(%6.2f %6.2f %6.2f) rotHi=(%6.2f %6.2f %6.2f) rotSpr=(%5.1f %5.1f %5.1f) posLo=%s posHi=%s posSpr=%s" %
              (j.name_jp, j.rb_a, j.rb_b, *j.rot_lo, *j.rot_hi, *j.rot_spring,
               j.pos_lo, j.pos_hi, j.pos_spring))
