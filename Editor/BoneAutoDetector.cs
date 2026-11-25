using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Paratap.KemoLegAim.Editor
{
    /// <summary>
    /// ボーン自動検出（仕様 §5）。
    /// - トラッキング側: Animator.GetBoneTransform(HumanBodyBones) から取得（名前非依存）。
    /// - ダミー側: Humanoid 未割り当てボーンから、識別語（A群=重み高 / B群=重み中）、
    ///   部位語、左右、親子チェーン、トラッキング側との近傍性をスコアリングして推定する。
    /// </summary>
    public static class BoneAutoDetector
    {
        // 識別語 A群（Dummy と同義・代替。重み高）
        private static readonly string[] IdentifierWordsA =
        {
            "dummy", "fake", "mock", "pseudo", "proxy", "virtual", "ghost",
            "alt", "alternate", "substitute", "imitation"
        };

        // 識別語 B群（メッシュ／見た目と結びつく語。重み中）
        private static readonly string[] IdentifierWordsB =
        {
            "mesh", "visual", "model", "render", "skin", "geo", "geometry",
            "appearance", "shape", "surface", "look"
        };

        private const float ScorePartWord = 40f;
        private const float ScoreIdentifierA = 30f;
        private const float ScoreIdentifierB = 15f;
        private const float ScoreSideKnown = 10f;
        private const float ScoreProximityNear = 15f;
        private const float ScoreProximityFar = 5f;
        private const float ScoreChainLink = 20f;
        private const float ScoreDirectChild = 8f;
        private const float ScoreToeBonus = 10f;

        // この値未満のチェーンは「確信度が低い」として警告する
        private const float ConfidenceThreshold = 90f;

        public class Result
        {
            public bool FoundAnimator;
            public readonly List<string> Warnings = new List<string>();
        }

        private class Candidate
        {
            public Transform Bone;
            public LegPart Part;
            public float Score;
        }

        public static Animator FindAnimator(KemoLegAimSetup setup)
        {
            if (setup == null) return null;
            return setup.GetComponentInParent<Animator>() ?? setup.GetComponentInChildren<Animator>();
        }

        public static Transform SafeGetBone(Animator animator, HumanBodyBones bone)
        {
            if (animator == null) return null;
            try
            {
                return animator.GetBoneTransform(bone);
            }
            catch (Exception)
            {
                // Humanoid でない Avatar 等では例外になりうる。未割り当てとして扱う。
                return null;
            }
        }

        /// <summary>Humanoid に割り当てられている全ボーンの集合。</summary>
        public static HashSet<Transform> CollectHumanoidBones(Animator animator)
        {
            var set = new HashSet<Transform>();
            if (animator == null) return set;
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                var t = SafeGetBone(animator, bone);
                if (t != null) set.Add(t);
            }
            return set;
        }

        /// <summary>
        /// 検出を実行し、結果を setup のフィールドへ割り当てる。
        /// overwrite=false の場合、既に割り当て済みのダミーチェーンは上書きしない
        /// （アタッチ直後の自動実行用）。ボタンからの実行では overwrite=true を使う。
        /// </summary>
        public static Result Detect(KemoLegAimSetup setup, bool overwrite)
        {
            var result = new Result();
            var animator = FindAnimator(setup);
            if (animator == null)
            {
                result.Warnings.Add("Animator が見つかりません。アバター配下にコンポーネントを置いてください。");
                return result;
            }
            result.FoundAnimator = true;

            // ---- トラッキング側（Humanoid から自動取得して保持） ----
            setup.Hips = SafeGetBone(animator, HumanBodyBones.Hips);
            FillTracking(setup.TrackingLeft, animator,
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg,
                HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes);
            FillTracking(setup.TrackingRight, animator,
                HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg,
                HumanBodyBones.RightFoot, HumanBodyBones.RightToes);

            // ---- ダミー側 ----
            var humanoidBones = CollectHumanoidBones(animator);
            var all = animator.gameObject.GetComponentsInChildren<Transform>(true);

            foreach (var side in new[] { LegSide.Left, LegSide.Right })
            {
                var dummyChain = setup.GetDummyChain(side);
                if (!overwrite && !dummyChain.IsEmpty) continue;

                var found = FindDummyChain(all, humanoidBones, setup.GetTrackingChain(side), side, result);
                if (found != null)
                {
                    dummyChain.UpperLeg = found.UpperLeg;
                    dummyChain.LowerLeg = found.LowerLeg;
                    dummyChain.Foot = found.Foot;
                    dummyChain.Toe = found.Toe;
                }
                else
                {
                    result.Warnings.Add($"{SideLabel(side)}側のダミーボーンチェーンを検出できませんでした。手動で割り当ててください。");
                }
            }

            // ---- ターゲット初期位置の生成（仕様 v1.1 付記B） ----
            // 未初期化の場合のみ。初期化済みのユーザー編集値は再検出で上書きしない
            // （再生成は Inspector の「初期値を再計算」ボタンでのみ行う）。
            TargetPlacement.InitializeTargetPositions(setup, animator.transform, force: false);

            return result;
        }

        private static void FillTracking(LegChain chain, Animator animator,
            HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones foot, HumanBodyBones toe)
        {
            chain.UpperLeg = SafeGetBone(animator, upper);
            chain.LowerLeg = SafeGetBone(animator, lower);
            chain.Foot = SafeGetBone(animator, foot);
            chain.Toe = SafeGetBone(animator, toe);
        }

        // ---------------------------------------------------------------
        // ダミーチェーン検出
        // ---------------------------------------------------------------

        private static LegChain FindDummyChain(Transform[] all, HashSet<Transform> humanoidBones,
            LegChain trackingChain, LegSide side, Result result)
        {
            var candidates = new List<Candidate>();
            foreach (var t in all)
            {
                if (t == null || humanoidBones.Contains(t)) continue;
                var part = ClassifyPart(t.name);
                if (part == null) continue;
                var boneSide = DetectSide(t.name);
                if (boneSide != null && boneSide.Value != side) continue;

                float score = ScorePartWord + IdentifierScore(t.name);
                if (boneSide != null) score += ScoreSideKnown;
                score += ProximityScore(t, trackingChain.Get(part.Value));
                candidates.Add(new Candidate { Bone = t, Part = part.Value, Score = score });
            }

            // UpperLeg 候補を起点にチェーンを組み立て、合計スコア最大のものを選ぶ
            LegChain bestChain = null;
            float bestTotal = float.MinValue;

            foreach (var upper in candidates.Where(c => c.Part == LegPart.UpperLeg).OrderByDescending(c => c.Score).Take(5))
            {
                var chain = new LegChain { UpperLeg = upper.Bone };
                float total = upper.Score;

                var lower = FindBestDescendant(candidates, upper.Bone, LegPart.LowerLeg);
                if (lower != null)
                {
                    chain.LowerLeg = lower.Bone;
                    total += lower.Score + ScoreChainLink;

                    var foot = FindBestDescendant(candidates, lower.Bone, LegPart.Foot);
                    if (foot != null)
                    {
                        chain.Foot = foot.Bone;
                        total += foot.Score + ScoreChainLink;

                        var toe = FindBestDescendant(candidates, foot.Bone, LegPart.Toe);
                        if (toe != null)
                        {
                            chain.Toe = toe.Bone;
                            total += ScoreToeBonus; // 末端 Toe は確からしさとして加点（仕様 §5）
                        }
                    }
                }

                if (total > bestTotal)
                {
                    bestTotal = total;
                    bestChain = chain;
                }
            }

            if (bestChain == null) return null;

            if (bestChain.LowerLeg == null || bestChain.Foot == null)
            {
                result.Warnings.Add($"{SideLabel(side)}側のダミーチェーンが不完全です（Upper→Lower→Foot の3段が揃いませんでした）。");
            }
            if (bestTotal < ConfidenceThreshold)
            {
                result.Warnings.Add($"{SideLabel(side)}側のダミー検出の確信度が低めです（スコア {bestTotal:0}）。割り当てを確認してください。");
            }
            return bestChain;
        }

        /// <summary>
        /// parent の子孫（深さ3まで）から、指定部位の最良候補を探す。
        /// 直接の子にはボーナスを付ける（通常ボーンチェーンは直接の親子）。
        /// </summary>
        private static Candidate FindBestDescendant(List<Candidate> candidates, Transform parent, LegPart part)
        {
            Candidate best = null;
            float bestScore = float.MinValue;
            foreach (var c in candidates)
            {
                if (c.Part != part) continue;
                int depth = DepthBelow(parent, c.Bone, 3);
                if (depth < 0) continue;
                float score = c.Score + (depth == 1 ? ScoreDirectChild : 0f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>bone が ancestor の子孫なら深さ（直接の子=1）を、そうでなければ -1 を返す。</summary>
        private static int DepthBelow(Transform ancestor, Transform bone, int maxDepth)
        {
            int depth = 0;
            var current = bone;
            while (current != null && current != ancestor && depth <= maxDepth)
            {
                current = current.parent;
                depth++;
            }
            return current == ancestor && depth <= maxDepth ? depth : -1;
        }

        private static float ProximityScore(Transform bone, Transform trackingBone)
        {
            if (bone == null || trackingBone == null) return 0f;
            // 脚全体の長さの目安として 0.5m を基準にした簡易判定。
            // TODO 実地確認: アバターのスケール差が大きい場合の閾値調整。
            float d = Vector3.Distance(bone.position, trackingBone.position);
            if (d < 0.4f) return ScoreProximityNear;
            if (d < 0.8f) return ScoreProximityFar;
            return 0f;
        }

        // ---------------------------------------------------------------
        // 名前の分類
        // ---------------------------------------------------------------

        /// <summary>ボーン名から部位を推定する。推定できなければ null。</summary>
        public static LegPart? ClassifyPart(string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return null;
            string n = Normalize(boneName);
            if (n.Contains("upperleg") || n.Contains("upleg") || n.Contains("thigh")) return LegPart.UpperLeg;
            if (n.Contains("lowerleg") || n.Contains("lowleg") || n.Contains("shin") ||
                n.Contains("calf") || n.Contains("knee")) return LegPart.LowerLeg;
            if (n.Contains("toe")) return LegPart.Toe;
            if (n.Contains("foot") || n.Contains("ankle")) return LegPart.Foot;
            return null;
        }

        /// <summary>ボーン名から左右を推定する。推定できなければ null。</summary>
        public static LegSide? DetectSide(string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return null;
            var tokens = SplitTokens(boneName);
            foreach (var token in tokens)
            {
                if (token == "l" || token == "left") return LegSide.Left;
                if (token == "r" || token == "right") return LegSide.Right;
            }
            return null;
        }

        /// <summary>識別語スコア。A群に一致すれば高、B群なら中、どちらでもなければ 0。</summary>
        public static float IdentifierScore(string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return 0f;
            string n = Normalize(boneName);
            foreach (var w in IdentifierWordsA) if (n.Contains(w)) return ScoreIdentifierA;
            foreach (var w in IdentifierWordsB) if (n.Contains(w)) return ScoreIdentifierB;
            return 0f;
        }

        private static string Normalize(string name)
        {
            var chars = name.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars).ToLowerInvariant();
        }

        private static List<string> SplitTokens(string name)
        {
            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(char.ToLowerInvariant(c));
                }
                else if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }

        public static string SideLabel(LegSide side) => side == LegSide.Left ? "左" : "右";

        public static string PartLabel(LegPart part)
        {
            switch (part)
            {
                case LegPart.UpperLeg: return "UpperLeg";
                case LegPart.LowerLeg: return "LowerLeg";
                case LegPart.Foot: return "Foot";
                case LegPart.Toe: return "Toe";
                default: return part.ToString();
            }
        }
    }
}
