using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Paratap.KemoLegAim.Editor
{
    /// <summary>
    /// NDMF ビルド時にクローン側へセットアップを適用する本体（仕様 §3 / §4、v1.1 付記A / B）。
    /// - ターゲット用空オブジェクトを生成し、1つ下の関節側のトラッキングボーンの子にする
    ///   （Upper の親 = LowerLeg、Lower の親 = Foot、Foot の親 = Foot。付記A）
    /// - ターゲットのローカル Position は setup のフィールド値をそのまま使う
    ///   （ビルド時にワールド計算はしない。付記B）
    /// - ダミー側 Upper/Lower/Foot（左右計6）に VRCAimConstraint を付与・設定・有効化する
    /// setup のフィールド参照はクローン側の Transform を指している
    /// （NDMF はアバターを複製してからパスを実行するため、参照も複製側に解決される）。
    /// </summary>
    public static class KemoLegAimBuilder
    {
        public static void Apply(KemoLegAimSetup setup)
        {
            if (setup == null) return;

            var animator = BoneAutoDetector.FindAnimator(setup);

            if (!setup.TargetPositionsInitialized)
            {
                Debug.LogWarning(
                    "[KemoLegAim] ターゲット位置の初期値が生成されていません。" +
                    "フィールド値（未初期化の場合は 0）がそのまま使われます。" +
                    "Inspector で自動検出または「初期値を再計算」を実行してください。", setup);
            }

            var hips = setup.Hips;
            if (hips == null && animator != null)
            {
                hips = BoneAutoDetector.SafeGetBone(animator, HumanBodyBones.Hips);
            }

            foreach (var side in new[] { LegSide.Left, LegSide.Right })
            {
                var tracking = setup.GetTrackingChain(side);
                var dummy = setup.GetDummyChain(side);

                // フィールドが空の部位は Humanoid 割り当てから補完する
                if (animator != null)
                {
                    tracking = FillTrackingFromHumanoid(tracking, animator, side);
                }

                foreach (var part in new[] { LegPart.UpperLeg, LegPart.LowerLeg, LegPart.Foot })
                {
                    var dummyBone = dummy.Get(part);
                    var trackingBone = tracking.Get(part);
                    // 親は「対応するボーン」ではなく1つ下の関節側（付記A）
                    var parentBone = TargetPlacement.GetParentBone(tracking, part);
                    if (dummyBone == null || trackingBone == null || parentBone == null)
                    {
                        Debug.LogWarning(
                            $"[KemoLegAim] {BoneAutoDetector.SideLabel(side)} {BoneAutoDetector.PartLabel(part)}: " +
                            "ボーン割り当てが不足しているためスキップしました。", setup);
                        continue;
                    }

                    // ---- ターゲット用空オブジェクト生成 ----
                    string targetName = setup.GetTargetName(side, part);
                    if (string.IsNullOrEmpty(targetName))
                    {
                        targetName = KemoLegTargetName.Generate(dummyBone.name);
                    }

                    var target = new GameObject(targetName);
                    target.transform.SetParent(parentBone, false);
                    // フィールド値（親ローカル Position）をそのまま設定する（付記B）
                    target.transform.localPosition = setup.GetTargetLocalPosition(side, part);
                    target.transform.localRotation = Quaternion.identity;
                    target.transform.localScale = Vector3.one;

                    // ---- VRC Aim Constraint 付与・設定 ----
                    var constraint = dummyBone.gameObject.AddComponent<VRCAimConstraint>();
                    constraint.WorldUp = VRCConstraintBase.WorldUpType.ObjectUp;
                    if (hips != null) constraint.WorldUpTransform = hips;
                    // TODO 実地確認: AimAxis / UpAxis は元手順で変更しないため既定値のままにしている。
                    //   既定値が元手順の Inspector 初期状態と一致するか、実プロジェクトで確認すること。

#if KEMOLEG_SDK_3_10_6_OR_NEWER
                    constraint.AddSource(new VRCConstraintSource(target.transform, 1.0f));
#else
                    // フォールバック（SDK 3.10.6 未満、ベストエフォート。仕様 §10）:
                    // Sources リストを直接操作する。
                    // TODO 実地確認: 旧 SDK における VRCConstraintSourceKeyableList の
                    //   正確な操作 API（Add の可否・反映方法）を旧 SDK 環境で確認すること。
                    constraint.Sources.Add(new VRCConstraintSource(target.transform, 1.0f));
#endif

                    // TODO 実地確認: ActivateConstraint() と ApplyConfigurationChanges() の
                    //   正しい呼び出し順序は調査で未確定。ここでは公式 API ページの
                    //   元手順対応表の並び（Activate → 仕上げに Apply）に倣っている。
                    //   実プロジェクトで逆順との差異を検証すること。
                    constraint.ActivateConstraint();
                    constraint.ApplyConfigurationChanges();
                }
            }
        }

        private static LegChain FillTrackingFromHumanoid(LegChain fieldChain, Animator animator, LegSide side)
        {
            var filled = new LegChain
            {
                UpperLeg = fieldChain.UpperLeg,
                LowerLeg = fieldChain.LowerLeg,
                Foot = fieldChain.Foot,
                Toe = fieldChain.Toe
            };

            if (side == LegSide.Left)
            {
                if (filled.UpperLeg == null) filled.UpperLeg = BoneAutoDetector.SafeGetBone(animator, HumanBodyBones.LeftUpperLeg);
                if (filled.LowerLeg == null) filled.LowerLeg = BoneAutoDetector.SafeGetBone(animator, HumanBodyBones.LeftLowerLeg);
                if (filled.Foot == null) filled.Foot = BoneAutoDetector.SafeGetBone(animator, HumanBodyBones.LeftFoot);
                if (filled.Toe == null) filled.Toe = BoneAutoDetector.SafeGetBone(animator, HumanBodyBones.LeftToes);
            }
            else
            {
                if (filled.UpperLeg == null) filled.UpperLeg = BoneAutoDetector.SafeGetBone(animator, HumanBodyBones.RightUpperLeg);
                if (filled.LowerLeg == null) filled.LowerLeg = BoneAutoDetector.SafeGetBone(animator, HumanBodyBones.RightLowerLeg);
                if (filled.Foot == null) filled.Foot = BoneAutoDetector.SafeGetBone(animator, HumanBodyBones.RightFoot);
                if (filled.Toe == null) filled.Toe = BoneAutoDetector.SafeGetBone(animator, HumanBodyBones.RightToes);
            }
            return filled;
        }
    }
}
