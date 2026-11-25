using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Paratap.KemoLegAim.Editor
{
    /// <summary>
    /// 生成したターゲットと Constraint の組（部位ごと）。
    /// </summary>
    public class CreatedConstraint
    {
        public VRCAimConstraint Constraint;
        public LegSide Side;
        public LegPart Part;
        public Transform DummyBone;
        public Transform Target;
        public Transform Hips;
    }

    /// <summary>
    /// NDMF ビルド時にクローン側へセットアップを適用する本体（仕様 §3 / §4、v1.1 付記A / B）。
    /// - ターゲット用空オブジェクトを生成し、1つ下の関節側のトラッキングボーンの子にする
    ///   （Upper の親 = LowerLeg、Lower の親 = Foot、Foot の親 = Foot。付記A）
    /// - ターゲットのローカル Position は setup のフィールド値をそのまま使う
    ///   （ビルド時にワールド計算はしない。付記B）
    /// - ダミー側 Upper/Lower/Foot（左右計6）に VRCAimConstraint を付与・設定・有効化する
    /// setup のフィールド参照はクローン側の Transform を指している
    /// （NDMF はアバターを複製してからパスを実行するため、参照も複製側に解決される）。
    ///
    /// At Rest / Offset の扱い（v0.4.0）:
    /// ActivateConstraint() はビルド文脈では Offset をキャプチャしない
    /// （v0.3.0 で実測。現在のオフセット＝既定値 (0,0,0) を維持してロックするだけ）。
    /// 手動セットアップで値が決まるのは、未ロックの Constraint がエディタ上で
    /// 再計算を繰り返すため。そこで正規ルートは Editor 側の事前キャプチャ
    /// （KemoLegAimCapture が一時クローン上で再計算させて setup へ保存）とし、
    /// ビルド時は保存値をそのまま設定する。未キャプチャの場合は v0.3.0 の
    /// 計算値フォールバックで代用する（姿勢は近似になる）。
    /// </summary>
    public static class KemoLegAimBuilder
    {
        public static void Apply(KemoLegAimSetup setup)
        {
            if (setup == null) return;

            if (setup.OffsetsCaptured && !setup.CapturedPositionsMatch())
            {
                Debug.LogWarning(
                    "[KemoLegAim] ターゲット位置がキャプチャ時点から変更されています。" +
                    "At Rest / Offset が古い可能性があるため、Inspector で再キャプチャしてください。", setup);
            }

            var created = CreateAll(setup);
            foreach (var c in created)
            {
                FinalizeConstraint(setup, c);
            }
        }

        /// <summary>
        /// ターゲット生成と Constraint の付与・基本設定までを行い、
        /// At Rest / Offset は未確定（既定値）のまま返す。
        /// ビルド時の Apply と、Editor 側キャプチャ（KemoLegAimCapture）の両方から使う。
        /// </summary>
        public static List<CreatedConstraint> CreateAll(KemoLegAimSetup setup)
        {
            var result = new List<CreatedConstraint>();
            if (setup == null) return result;

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

                    // ---- VRC Aim Constraint 付与・基本設定 ----
                    var constraint = dummyBone.gameObject.AddComponent<VRCAimConstraint>();
                    constraint.WorldUp = VRCConstraintBase.WorldUpType.ObjectUp;
                    if (hips != null) constraint.WorldUpTransform = hips;
                    // Aim / Up Vector は手動セットアップの実測値（2026-11-26）に合わせる。
                    // Aim は全部位 (0,0,1)。Up は LowerLeg のみ (0,0,1)、他は (0,1,0)。
                    constraint.AimAxis = new Vector3(0f, 0f, 1f);
                    constraint.UpAxis = part == LegPart.LowerLeg
                        ? new Vector3(0f, 0f, 1f)
                        : new Vector3(0f, 1f, 0f);

#if KEMOLEG_SDK_3_10_6_OR_NEWER
                    constraint.AddSource(new VRCConstraintSource(target.transform, 1.0f));
#else
                    // フォールバック（SDK 3.10.6 未満、ベストエフォート。仕様 §10）:
                    // Sources リストを直接操作する。
                    // TODO 実地確認: 旧 SDK における VRCConstraintSourceKeyableList の
                    //   正確な操作 API（Add の可否・反映方法）を旧 SDK 環境で確認すること。
                    constraint.Sources.Add(new VRCConstraintSource(target.transform, 1.0f));
#endif

                    constraint.ApplyConfigurationChanges();

                    result.Add(new CreatedConstraint
                    {
                        Constraint = constraint,
                        Side = side,
                        Part = part,
                        DummyBone = dummyBone,
                        Target = target.transform,
                        Hips = hips
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// At Rest / Offset を確定し、有効化＋ロックする（ビルド時専用）。
        /// キャプチャ済みなら保存値を、未キャプチャなら計算値フォールバックを使う。
        /// </summary>
        private static void FinalizeConstraint(KemoLegAimSetup setup, CreatedConstraint c)
        {
            var constraint = c.Constraint;

            if (setup.OffsetsCaptured)
            {
                constraint.RotationAtRest = setup.GetCapturedAtRest(c.Side, c.Part);
                constraint.RotationOffset = setup.GetCapturedOffset(c.Side, c.Part);
                constraint.ApplyConfigurationChanges();
                constraint.ActivateConstraint();
                return;
            }

            // 未キャプチャ時のフォールバック（v0.3.0 方式）:
            // At Rest は定義どおりボーンのローカル回転。Offset は Activate で
            // キャプチャされないため、レストポーズの変換から計算した値で補う。
            // 計算値は SDK ソルバの合成と完全には一致せず、姿勢は近似になる。
            constraint.RotationAtRest = c.DummyBone.localRotation.eulerAngles;
            constraint.ApplyConfigurationChanges();
            constraint.ActivateConstraint();

            if (constraint.RotationOffset == Vector3.zero)
            {
                constraint.RotationOffset = ComputeAimOffset(
                    c.DummyBone, c.Target,
                    constraint.AimAxis, constraint.UpAxis, c.Hips).eulerAngles;
                constraint.ApplyConfigurationChanges();
                Debug.LogWarning(
                    $"[KemoLegAim] {BoneAutoDetector.SideLabel(c.Side)} {BoneAutoDetector.PartLabel(c.Part)}: " +
                    "At Rest / Offset が未キャプチャのため計算値で補いました。" +
                    "厳密な一致には Inspector での事前キャプチャを実行してください。", setup);
            }
        }

        /// <summary>
        /// レストポーズで「現在のボーンのワールド回転」と一致する結果になる Aim の
        /// RotationOffset（ワールド空間）を計算する。ソルバの合成を
        /// final = aimWorld * offset とみなし、offset = inverse(aimWorld) * currentWorld
        /// とする（Activate 時のキャプチャと同じ考え方）。
        /// aimWorld は、AimAxis がボーン→ターゲット方向を向き、UpAxis がワールドアップ
        /// （ObjectUp の場合は Hips の up）に合う回転として LookRotation で構成する。
        /// AimAxis と UpAxis が平行な部位（LowerLeg）では基準基底が定まらないため、
        /// UpAxis と平行でない軸を代用する（この場合のロール方向は近似になる）。
        /// 実測では手動セットアップの Offset と数十度単位でずれることが分かっており、
        /// あくまで未キャプチャ時の代用である。
        /// </summary>
        private static Quaternion ComputeAimOffset(Transform bone, Transform target, Vector3 aimAxis, Vector3 upAxis, Transform worldUpObject)
        {
            var dir = target.position - bone.position;
            if (dir.sqrMagnitude < 1e-12f) return Quaternion.identity;
            dir.Normalize();

            var worldUp = worldUpObject != null ? worldUpObject.up : Vector3.up;

            var basisUp = upAxis;
            if (Vector3.Cross(aimAxis, basisUp).sqrMagnitude < 1e-8f)
            {
                basisUp = Math.Abs(aimAxis.y) < 0.9f ? Vector3.up : Vector3.right;
            }

            var baseAim = Quaternion.LookRotation(aimAxis, basisUp);
            var targetAim = Quaternion.LookRotation(dir, worldUp);
            var aimWorld = targetAim * Quaternion.Inverse(baseAim);
            return Quaternion.Inverse(aimWorld) * bone.rotation;
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
