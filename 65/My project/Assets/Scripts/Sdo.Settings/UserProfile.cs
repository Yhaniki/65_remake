using System;
using System.Collections.Generic;

namespace Sdo.Settings
{
    [Serializable]
    public class AvatarOutfit
    {
        public string face = "";
        public string hair = "";
        public string coat = "";
        public string pant = "";
        public string shoes = "";
        public string hand = "";

        public static AvatarOutfit FromParts(string[] parts)
        {
            var o = new AvatarOutfit();
            o.FillMissing(parts);
            return o;
        }

        public void FillMissing(string[] parts)
        {
            if (parts == null) return;
            if (parts.Length > 0 && string.IsNullOrEmpty(face)) face = parts[0];
            if (parts.Length > 1 && string.IsNullOrEmpty(hair)) hair = parts[1];
            if (parts.Length > 2 && string.IsNullOrEmpty(coat)) coat = parts[2];
            if (parts.Length > 3 && string.IsNullOrEmpty(pant)) pant = parts[3];
            if (parts.Length > 4 && string.IsNullOrEmpty(shoes)) shoes = parts[4];
            if (parts.Length > 5 && string.IsNullOrEmpty(hand)) hand = parts[5];
            Clean();
        }

        public string[] ToParts()
        {
            Clean();
            return new[] { face, hair, coat, pant, shoes, hand };
        }

        public bool HasGenderMismatch(int gender)
        {
            var parts = ToParts();
            for (int i = 0; i < parts.Length; i++)
            {
                string u = (parts[i] ?? "").ToUpperInvariant();
                if (gender == 1 && u.Contains("_WOMAN_")) return true;
                if (gender != 1 && u.Contains("_MAN_")) return true;
            }
            return false;
        }

        private void Clean()
        {
            face = UserProfile.NormalizeClothPath(face);
            hair = UserProfile.NormalizeClothPath(hair);
            coat = UserProfile.NormalizeClothPath(coat);
            pant = UserProfile.NormalizeClothPath(pant);
            shoes = UserProfile.NormalizeClothPath(shoes);
            hand = UserProfile.NormalizeClothPath(hand);
        }
    }

    [Serializable]
    public class UserProfile
    {
        public string id = "00000000";
        public string name = "玩家001";
        public int gender = 0;
        public int avatarId = 0;
        public string[] ownedClothes = new string[0];
        public AvatarOutfit equippedClothes = new AvatarOutfit();
        public string createdAt = "";
        public string lastPlayedAt = "";

        public UserProfile() { }

        public UserProfile(string id, string name, int gender)
        {
            this.id = id;
            this.name = name;
            this.gender = gender;
        }

        public UserProfile Sanitize()
        {
            if (string.IsNullOrEmpty(id)) id = "00000000";
            if (string.IsNullOrEmpty(name)) name = "玩家001";
            gender = gender == 1 ? 1 : 0;
            if (avatarId < 0) avatarId = 0;
            EnsureWardrobe();
            return this;
        }

        public string[] EquippedAvatarParts()
        {
            Sanitize();
            return Clone(equippedClothes.ToParts());
        }

        private void EnsureWardrobe()
        {
            var defaults = DefaultClothesForGender(gender);
            if (equippedClothes == null || equippedClothes.HasGenderMismatch(gender))
                equippedClothes = AvatarOutfit.FromParts(defaults);
            else
                equippedClothes.FillMissing(defaults);

            var owned = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddClothes(owned, seen, ownedClothes);
            AddClothes(owned, seen, defaults);
            AddClothes(owned, seen, equippedClothes.ToParts());
            ownedClothes = owned.ToArray();
        }

        private static void AddClothes(List<string> dst, HashSet<string> seen, string[] src)
        {
            if (src == null) return;
            for (int i = 0; i < src.Length; i++)
            {
                string rel = NormalizeClothPath(src[i]);
                if (string.IsNullOrEmpty(rel)) continue;
                if (seen.Add(rel)) dst.Add(rel);
            }
        }

        internal static string NormalizeClothPath(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "";
            rel = rel.Trim().Replace('\\', '/');
            if (rel.Length == 0) return "";
            if (rel.IndexOf('/') < 0) rel = "AVATAR/" + rel;
            if (!rel.EndsWith(".MSH", StringComparison.OrdinalIgnoreCase)) rel += ".MSH";
            return rel;
        }

        private static string[] Clone(string[] src)
        {
            if (src == null) return new string[0];
            var dst = new string[src.Length];
            Array.Copy(src, dst, src.Length);
            return dst;
        }

        public static string[] DefaultClothesForGender(int gender)
        {
            return gender == 1 ? new[]
            {
                "AVATAR/900001_MAN_FACE.MSH",
                "AVATAR/900002_MAN_HAIR.MSH",
                "AVATAR/900003_MAN_COAT.MSH",
                "AVATAR/900004_MAN_PANT.MSH",
                "AVATAR/900006_MAN_SHOES.MSH",
                "AVATAR/900005_MAN_HAND.MSH",
            } : new[]
            {
                "AVATAR/900007_WOMAN_FACE.MSH",
                "AVATAR/900017_WOMAN_HAIR.MSH",
                "AVATAR/900018_WOMAN_COAT.MSH",
                "AVATAR/900019_WOMAN_PANT.MSH",
                "AVATAR/900020_WOMAN_SHOES.MSH",
                "AVATAR/900011_WOMAN_HAND.MSH",
            };
        }
    }
}
