using System.Collections.Generic;
using UnityEngine;

namespace Paratap.KemoLegAim.Editor
{
    public enum HumanoidCheckIssueKind
    {
        Generic,
        TrackingMissing,
        TrackingMismatch,
        DummyAssignedToHumanoid,
        DummyLikeNameInHumanoid
    }

    /// <summary>Humanoid 割り当てチェックの警告1件（仕様 §8）。</summary>
    public class HumanoidCheckIssue
    {
        public HumanoidCheckIssueKind Kind;
        public string Message;
        /// <summary>Inspector の「選択」ボタンや NDMF レポートの参照に使うボーン。null の場合もある。</summary>
        public Transform Bone;
        /// <summary>関係する Humanoid ボーン名（例: LeftLowerLeg）。該当する場合のみ。</summary>
        public string HumanoidBoneName;
        /// <summary>部位ラベル（例: 左 LowerLeg）。</summary>
        public string PartLabel;
    }

    /// <summary>
    /// Humanoid Rig 割り当てチェック（仕様 §8）。
    /// Inspector 表示と NDMF プラグイン（Resolving）の双方から使う。
    /// チェックのみで、割り当ての自動付け替えは行わない。
    /// </summary>
    public static class HumanoidCheck
    {
        private static readonly (HumanBodyBones Bone, string Name)[] LegHumanoidBones =
        {
            (HumanBodyBones.Hips, "Hips"),
            (HumanBodyBones.LeftUpperLeg, "LeftUpperLeg"),
            (HumanBodyBones.LeftLowerLeg, "LeftLowerLeg"),
            (HumanBodyBones.LeftFoot, "LeftFoot"),
            (HumanBodyBones.LeftToes, "LeftToes"),
            (HumanBodyBones.RightUpperLeg, "RightUpperLeg"),
            (HumanBodyBones.RightLowerLeg, "RightLowerLeg"),
            (HumanBodyBones.RightFoot, "RightFoot"),
            (HumanBodyBones.RightToes, "RightToes"),
        };

        public static List<HumanoidCheckIssue> Run(KemoLegAimSetup setup)
        {
            var issues = new List<HumanoidCheckIssue>();
            if (setup == null) return issues;

            var animator = BoneAutoDetector.FindAnimator(setup);
            if (animator == null)
            {
                issues.Add(new HumanoidCheckIssue
                {
                    Kind = HumanoidCheckIssueKind.Generic,
                    Message = "Animator が見つかりません。アバター配下にコンポーネントを置いてください。",
                    Bone = setup.transform,
                    PartLabel = "全般"
                });
                return issues;
            }

            // Humanoid に実際に割り当てられているボーン（脚＋Hips）
            var assigned = new Dictionary<Transform, string>();
            var assignedByBone = new Dictionary<HumanBodyBones, Transform>();
            foreach (var (bone, name) in LegHumanoidBones)
            {
                var t = BoneAutoDetector.SafeGetBone(animator, bone);
                assignedByBone[bone] = t;
                if (t != null && !assigned.ContainsKey(t)) assigned[t] = name;
            }

            foreach (var side in new[] { LegSide.Left, LegSide.Right })
            {
                string sideLabel = BoneAutoDetector.SideLabel(side);
                var tracking = setup.GetTrackingChain(side);
                var dummy = setup.GetDummyChain(side);

                foreach (var part in new[] { LegPart.UpperLeg, LegPart.LowerLeg, LegPart.Foot, LegPart.Toe })
                {
                    string label = $"{sideLabel} {BoneAutoDetector.PartLabel(part)}";
                    var humanoidBone = ToHumanoidBone(side, part);
                    var actual = assignedByBone.TryGetValue(humanoidBone, out var ab) ? ab : null;
                    var field = tracking.Get(part);

                    // トラッキング側: Humanoid 未割り当て
                    if (actual == null)
                    {
                        issues.Add(new HumanoidCheckIssue
                        {
                            Kind = HumanoidCheckIssueKind.TrackingMissing,
                            Message = $"トラッキング側 {label} が Humanoid に割り当てられていません。",
                            Bone = animator.transform,
                            HumanoidBoneName = humanoidBone.ToString(),
                            PartLabel = label
                        });
                    }
                    // トラッキング側: フィールド保持値と Humanoid 割り当てが不一致
                    else if (field != null && field != actual)
                    {
                        issues.Add(new HumanoidCheckIssue
                        {
                            Kind = HumanoidCheckIssueKind.TrackingMismatch,
                            Message = $"トラッキング側 {label} のフィールド（{field.name}）が Humanoid 割り当て（{actual.name}）と一致しません。「自動検出」で再取得してください。",
                            Bone = field,
                            HumanoidBoneName = humanoidBone.ToString(),
                            PartLabel = label
                        });
                    }

                    // ダミー側: Humanoid に割り当てられてしまっている
                    var dummyBone = dummy.Get(part);
                    if (dummyBone != null && assigned.TryGetValue(dummyBone, out var hbName))
                    {
                        issues.Add(new HumanoidCheckIssue
                        {
                            Kind = HumanoidCheckIssueKind.DummyAssignedToHumanoid,
                            Message = $"ダミー側 {label}「{dummyBone.name}」が Humanoid の {hbName} に割り当てられています。",
                            Bone = dummyBone,
                            HumanoidBoneName = hbName,
                            PartLabel = label
                        });
                    }
                }

                // Humanoid の脚スロットに、ダミー候補名のボーンが割り当てられている
                // （元記事の手順で手作業確認していたポイント）
                foreach (var part in new[] { LegPart.UpperLeg, LegPart.LowerLeg, LegPart.Foot, LegPart.Toe })
                {
                    var humanoidBone = ToHumanoidBone(side, part);
                    var actual = assignedByBone.TryGetValue(humanoidBone, out var ab2) ? ab2 : null;
                    if (actual == null) continue;
                    if (BoneAutoDetector.IdentifierScore(actual.name) <= 0f) continue;
                    // 既にダミー側フィールドとの重複として報告済みなら二重報告しない
                    if (dummy.Get(part) == actual) continue;
                    string label = $"{sideLabel} {BoneAutoDetector.PartLabel(part)}";
                    issues.Add(new HumanoidCheckIssue
                    {
                        Kind = HumanoidCheckIssueKind.DummyLikeNameInHumanoid,
                        Message = $"Humanoid の {humanoidBone} にダミー候補名のボーン「{actual.name}」が割り当てられています。トラッキング用ボーンへの再割り当てを確認してください。",
                        Bone = actual,
                        HumanoidBoneName = humanoidBone.ToString(),
                        PartLabel = label
                    });
                }
            }

            return issues;
        }

        private static HumanBodyBones ToHumanoidBone(LegSide side, LegPart part)
        {
            if (side == LegSide.Left)
            {
                switch (part)
                {
                    case LegPart.UpperLeg: return HumanBodyBones.LeftUpperLeg;
                    case LegPart.LowerLeg: return HumanBodyBones.LeftLowerLeg;
                    case LegPart.Foot: return HumanBodyBones.LeftFoot;
                    case LegPart.Toe: return HumanBodyBones.LeftToes;
                }
            }
            else
            {
                switch (part)
                {
                    case LegPart.UpperLeg: return HumanBodyBones.RightUpperLeg;
                    case LegPart.LowerLeg: return HumanBodyBones.RightLowerLeg;
                    case LegPart.Foot: return HumanBodyBones.RightFoot;
                    case LegPart.Toe: return HumanBodyBones.RightToes;
                }
            }
            return HumanBodyBones.LastBone;
        }
    }
}
