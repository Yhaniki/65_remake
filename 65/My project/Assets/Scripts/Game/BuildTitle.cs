namespace Sdo.Game
{
    /// <summary>
    /// Pure formatter for the standalone window title (Unity's <c>PlayerSettings.productName</c>) from git state.
    /// Kept UnityEngine-free and in a runtime assembly so both the editor build script (BuildScript, which shells out
    /// to git) and the EditMode tests can call it.
    ///
    /// Layout:
    ///   on a tag                 -> "<product> <tag>"                 e.g. "dance v1.0.0"
    ///   after a tag (dev build)  -> "<product> <tag>-dev-<hash5>"     e.g. "dance v1.0.0-dev-719a9"
    ///   no tags but have a hash  -> "<product> dev-<hash5>"           e.g. "dance dev-719a9"
    ///   no git at all            -> "<product>"                       e.g. "dance"
    /// </summary>
    public static class BuildTitle
    {
        /// <param name="product">Base product name (the plain window title, e.g. "dance").</param>
        /// <param name="exactTag">`git describe --tags --exact-match HEAD` output; null/empty when HEAD is not on a tag.</param>
        /// <param name="nearestTag">`git describe --tags --abbrev=0` output; the closest ancestor tag, null/empty when none.</param>
        /// <param name="hash5">`git rev-parse --short=5 HEAD` output; the 5-char commit hash, null/empty when unavailable.</param>
        public static string Format(string product, string exactTag, string nearestTag, string hash5)
        {
            product    = Clean(product);
            exactTag   = Clean(exactTag);
            nearestTag = Clean(nearestTag);
            hash5      = Clean(hash5);

            if (!string.IsNullOrEmpty(exactTag))
                return Join(product, exactTag);
            if (!string.IsNullOrEmpty(nearestTag) && !string.IsNullOrEmpty(hash5))
                return Join(product, nearestTag + "-dev-" + hash5);
            if (!string.IsNullOrEmpty(hash5))
                return Join(product, "dev-" + hash5);
            return product;
        }

        private static string Join(string product, string version) =>
            string.IsNullOrEmpty(product) ? version : product + " " + version;

        private static string Clean(string s) => s == null ? null : s.Trim();
    }
}
