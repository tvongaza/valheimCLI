using UnityEngine;

namespace valheimCLI
{
    /// <summary>
    /// Whether a prefab is a container at all.
    ///
    /// Its own file so the rule can be tested against the real shipped code
    /// with two doubles, rather than by standing up the whole census.
    /// </summary>
    public static class ContainerLookup
    {
        /// <summary>
        /// The component is looked for in the CHILDREN as well as on the root,
        /// and including inactive objects, because not every container carries
        /// it on the root. The vanilla Cart is the case that found this: its
        /// Container sits on a child, so a root-only lookup reported no
        /// container at a cart holding 123 stone, before and after a save.
        ///
        /// Container is the question. Where the prefab hangs it is not, and a
        /// prefab asset is not an active scene object, so inactive children
        /// count too.
        /// </summary>
        public static bool HoldsAContainer(GameObject? prefab) =>
            prefab != null && prefab.GetComponentInChildren<Container>(true) != null;
    }
}
