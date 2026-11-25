using UnityEngine;

namespace Paratap.KemoLegAim.Editor
{
    /// <summary>
    /// ターゲット用空オブジェクトの配置に関する計算（仕様 v1.1 付記A / B）。
    ///
    /// v0.2.0 での役割:
    /// - 親ボーンの対応付け（付記A）: Upper の親 = トラッキング LowerLeg、
    ///   Lower の親 = トラッキング Foot、Foot の親 = トラッキング Foot。
    /// - 初期値の生成: 旧割合方式のワールド目標位置
    ///   （対応トラッキングボーン位置 + アバター前方 × 割合 × セグメント長）を
    ///   親ボーンのローカル座標へ換算する。
    /// - ワールド ↔ 親ローカルの相互変換（ギズモ表示位置の算出と、
    ///   ドラッグ結果の逆算に使う）。
    /// ビルド時の配置はフィールド値（ローカル Position）をそのまま使うため、
    /// 本クラスのワールド計算は初期値生成とギズモ専用になった。
    ///
    /// セグメント長はトラッキング側チェーンで算出する:
    ///   Upper = Upper-Lower 間 / Lower = Lower-Foot 間 / Foot = Foot-Toe 間
    /// </summary>
    public static class TargetPlacement
    {
        // セグメント長が算出できない場合のフォールバック (m)
        private const float FallbackSegmentLength = 0.1f;

        /// <summary>初期値生成に使う旧割合方式の既定割合（仕様 v1.1 付記B）。</summary>
        public static float DefaultRatio(LegPart part)
        {
            switch (part)
            {
                case LegPart.UpperLeg: return 0.25f;
                case LegPart.LowerLeg: return -0.25f;
                case LegPart.Foot: return 0.25f;
                default: return 0f; // Toe は対象外
            }
        }

        public static LegPart NextPart(LegPart part)
        {
            switch (part)
            {
                case LegPart.UpperLeg: return LegPart.LowerLeg;
                case LegPart.LowerLeg: return LegPart.Foot;
                case LegPart.Foot: return LegPart.Toe;
                default: return LegPart.Toe;
            }
        }

        /// <summary>
        /// ターゲットの親になる部位（仕様 v1.1 付記A）。
        /// ターゲットは「対応するボーン」ではなく1つ下の関節側に付ける。
        /// </summary>
        public static LegPart ParentPart(LegPart part)
        {
            switch (part)
            {
                case LegPart.UpperLeg: return LegPart.LowerLeg;
                case LegPart.LowerLeg: return LegPart.Foot;
                case LegPart.Foot: return LegPart.Foot;
                default: return part; // Toe は対象外
            }
        }

        /// <summary>ターゲットの親になるトラッキング側ボーン。</summary>
        public static Transform GetParentBone(LegChain trackingChain, LegPart part)
        {
            return trackingChain?.Get(ParentPart(part));
        }

        public static float SegmentLength(LegChain trackingChain, LegPart part)
        {
            if (trackingChain == null) return FallbackSegmentLength;
            var a = trackingChain.Get(part);
            var b = trackingChain.Get(NextPart(part));
            if (a == null || b == null) return FallbackSegmentLength;
            float len = Vector3.Distance(a.position, b.position);
            return len > 1e-5f ? len : FallbackSegmentLength;
        }

        /// <summary>
        /// 旧割合方式によるワールド目標位置（初期値生成専用）。
        /// 位置 = 対応トラッキングボーン位置 + アバター前方 × 割合 × セグメント長
        /// </summary>
        public static Vector3 ComputeWorldTargetPosition(Transform avatarRoot, LegChain trackingChain,
            LegPart part)
        {
            var bone = trackingChain?.Get(part);
            if (bone == null) return Vector3.zero;
            Vector3 forward = avatarRoot != null ? avatarRoot.forward : Vector3.forward;
            return bone.position + forward * (DefaultRatio(part) * SegmentLength(trackingChain, part));
        }

        /// <summary>
        /// ワールド座標を親ボーンのローカル座標へ換算する。
        /// Transform.InverseTransformPoint を使うため、親チェーンに
        /// スケールが含まれていても正しく換算される。
        /// </summary>
        public static Vector3 WorldToParentLocal(Transform parent, Vector3 worldPosition)
        {
            return parent != null ? parent.InverseTransformPoint(worldPosition) : worldPosition;
        }

        /// <summary>
        /// 親ボーンのローカル座標をワールド座標へ換算する
        /// （親の localToWorldMatrix × ローカル Position と等価）。
        /// </summary>
        public static Vector3 ParentLocalToWorld(Transform parent, Vector3 localPosition)
        {
            return parent != null
                ? parent.localToWorldMatrix.MultiplyPoint3x4(localPosition)
                : localPosition;
        }

        /// <summary>
        /// 初期値: 旧割合方式のワールド目標位置を親ローカルへ換算した値。
        /// 親ボーンまたは対応ボーンが無い場合は null（生成できない）。
        /// </summary>
        public static Vector3? ComputeInitialLocalPosition(Transform avatarRoot,
            LegChain trackingChain, LegPart part)
        {
            var parent = GetParentBone(trackingChain, part);
            if (parent == null || trackingChain.Get(part) == null) return null;
            Vector3 world = ComputeWorldTargetPosition(avatarRoot, trackingChain, part);
            return WorldToParentLocal(parent, world);
        }

        /// <summary>
        /// ターゲット6個の初期値を生成して setup に書き込む。
        /// force=false かつ初期化済み（TargetPositionsInitialized）の場合は
        /// ユーザー編集値を保護するため何もしない（再生成は「初期値を再計算」
        /// ボタン = force=true でのみ行う）。
        /// 戻り値: 値を書き込んだターゲットの数。
        /// TargetPositionsInitialized は6個すべて生成できた場合にのみ true になる
        /// （不足がある場合は次回の検出・再計算で再試行される）。
        /// </summary>
        public static int InitializeTargetPositions(KemoLegAimSetup setup, Transform avatarRoot,
            bool force)
        {
            if (setup == null) return 0;
            if (!force && setup.TargetPositionsInitialized) return 0;

            int written = 0;
            foreach (var side in new[] { LegSide.Left, LegSide.Right })
            {
                var tracking = setup.GetTrackingChain(side);
                foreach (var part in new[] { LegPart.UpperLeg, LegPart.LowerLeg, LegPart.Foot })
                {
                    var local = ComputeInitialLocalPosition(avatarRoot, tracking, part);
                    if (local == null) continue;
                    setup.SetTargetLocalPosition(side, part, local.Value);
                    written++;
                }
            }

            if (written == 6) setup.TargetPositionsInitialized = true;
            return written;
        }
    }
}
