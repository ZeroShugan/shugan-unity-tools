using System.Collections.Generic;
using UnityEngine;

namespace ZeroShugan.ShuganUnityTools
{
    // ─── Toe bones from another rig ──────────────────────────────────────────────
    // Some avatars already carry toe bones from another toe-tracking rig: Airi ships five Toe_*
    // Proximal/Intermediate/Distal chains per foot. Left in place, their weights compete with the toe
    // bones AutoRig Feet adds. The Blender script removes the bones it is sent and adds their weights onto
    // the nearest bone that stays, so no vertex loses its skinning.
    //
    // Port of find_foreign_toe_bones in shugan_autorig_feet.py; keep the two in step.
    // Kept: the foot bone, the avatar's own toe bone (the humanoid Toes, or else the only toe-named child
    // of the foot), their *_end tips, bones that are not toe-named (Karin's Shoes_tag_L), and AutoRig
    // Feet's own bones (the script removes those itself). Found: a toe-named bone below them whose head
    // sits in front of the foot joint, together with everything under it, so a whole chain goes at once.
    // On the Booth collection that is Airi's 40 chain bones and nothing on the other 16 bases.
    public static class ToeRigDetect
    {
        public static List<Transform> Find(Transform avatarRoot, Transform footL, Transform footR,
                                           Transform toeL, Transform toeR)
        {
            var found = new List<Transform>();
            Vector3 forward = avatarRoot != null ? avatarRoot.forward : Vector3.forward;
            foreach (var (foot, toe) in new[] { (footL, toeL), (footR, toeR) })
            {
                if (foot == null) continue;
                var keep = new HashSet<Transform> { foot };
                if (toe != null && toe != foot && toe.IsChildOf(foot))
                {
                    keep.Add(toe);
                }
                else
                {
                    Transform only = null;
                    int count = 0;
                    for (int i = 0; i < foot.childCount; i++)
                    {
                        var c = foot.GetChild(i);
                        if (HumanoidRigMapping.NameLooksLikeToe(c.name) && !IsEndBone(c.name) && !IsAutoRigBone(c.name))
                        {
                            only = c;
                            count++;
                        }
                    }
                    if (count == 1) keep.Add(only);
                }
                Walk(foot, false, foot.position, forward, keep, found);
            }
            return found;
        }

        // AutoRig Feet's own bones: removed by the script's own cleanup, never listed here.
        public static bool IsAutoRigBone(string name) =>
            name.StartsWith("z_CB ") || name.StartsWith("Toes_");

        static bool IsEndBone(string name)
        {
            string n = name.ToLowerInvariant();
            return n.EndsWith("_end") || n.EndsWith(".end") || n.EndsWith(" end");
        }

        static void Walk(Transform node, bool inChain, Vector3 footPos, Vector3 forward,
                         HashSet<Transform> keep, List<Transform> found)
        {
            for (int i = 0; i < node.childCount; i++)
            {
                var c = node.GetChild(i);
                if (IsAutoRigBone(c.name)) continue;
                if (inChain)
                {
                    found.Add(c);
                    Walk(c, true, footPos, forward, keep, found);
                }
                else if (keep.Contains(c))
                {
                    Walk(c, false, footPos, forward, keep, found);
                }
                else if (HumanoidRigMapping.NameLooksLikeToe(c.name) && !IsEndBone(c.name)
                         && c.GetComponent<Renderer>() == null
                         && Vector3.Dot(c.position - footPos, forward) > 0f)
                {
                    found.Add(c);
                    Walk(c, true, footPos, forward, keep, found);
                }
                else
                {
                    Walk(c, false, footPos, forward, keep, found);
                }
            }
        }
    }
}
