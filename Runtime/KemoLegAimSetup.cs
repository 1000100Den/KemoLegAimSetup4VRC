using System;
using UnityEngine;
using VRC.SDKBase;

namespace Paratap.KemoLegAim
{
    public enum LegSide
    {
        Left,
        Right
    }

    public enum LegPart
    {
        UpperLeg,
        LowerLeg,
        Foot,
        Toe
    }

    /// <summary>
    /// 片脚分のボーンチェーン（UpperLeg → LowerLeg → Foot → Toe）。
    /// トラッキング側・ダミー側の両方に使う。
    /// </summary>
    [Serializable]
    public class LegChain
    {
        public Transform UpperLeg;
        public Transform LowerLeg;
        public Transform Foot;
        public Transform Toe;

        public Transform Get(LegPart part)
        {
            switch (part)
            {
                case LegPart.UpperLeg: return UpperLeg;
                case LegPart.LowerLeg: return LowerLeg;
                case LegPart.Foot: return Foot;
                case LegPart.Toe: return Toe;
                default: return null;
            }
        }

        public void Set(LegPart part, Transform bone)
        {
            switch (part)
            {
                case LegPart.UpperLeg: UpperLeg = bone; break;
                case LegPart.LowerLeg: LowerLeg = bone; break;
                case LegPart.Foot: Foot = bone; break;
                case LegPart.Toe: Toe = bone; break;
            }
        }

        public bool IsEmpty =>
            UpperLeg == null && LowerLeg == null && Foot == null && Toe == null;
    }

    /// <summary>
    /// 鳥脚（獣脚）Aim Constraint セットアップの設定用コンポーネント。
    /// パラメータだけを保持し、実際の生成・適用は NDMF プラグインが
    /// ビルド時にクローン側へ行う（非破壊。仕様 §3）。
    /// IEditorOnly のため、ビルド後のアバターには残らない。
    /// </summary>
    [AddComponentMenu("KemoLeg Aim/KemoLeg Aim Setup")]
    public class KemoLegAimSetup : MonoBehaviour, IEditorOnly
    {
        // ---- ボーン参照 ----
        // トラッキング側は Humanoid 割り当てから自動取得されるが、
        // 検出結果を保持し、Inspector で確認・修正できるようにフィールドに持つ。
        public Transform Hips;

        public LegChain TrackingLeft = new LegChain();
        public LegChain TrackingRight = new LegChain();
        public LegChain DummyLeft = new LegChain();
        public LegChain DummyRight = new LegChain();

        // ---- ターゲット位置（仕様 v1.1 付記B） ----
        // ターゲット6個それぞれのローカル Position。基準は親ボーン
        // （Upper→トラッキング LowerLeg、Lower→トラッキング Foot、
        //   Foot→トラッキング Foot。仕様 v1.1 付記A）に対するローカル座標で、
        // Play 中にターゲット自身の Inspector に表示される Position と一致する。
        // 割合スライダー（旧 §7）は v0.2.0 で廃止し、この数値編集方式に置き換えた。
        // 範囲制限は付けない。
        public Vector3 TargetPositionUpperLeft;
        public Vector3 TargetPositionLowerLeft;
        public Vector3 TargetPositionFootLeft;
        public Vector3 TargetPositionUpperRight;
        public Vector3 TargetPositionLowerRight;
        public Vector3 TargetPositionFootRight;

        // 初期値（旧割合方式から換算した値）が生成済みかどうか。
        // true の場合、自動検出の再実行ではユーザー編集値を上書きしない
        // （再生成は Inspector の「初期値を再計算」ボタンでのみ行う）。
        public bool TargetPositionsInitialized;

        // ---- キャプチャ済み At Rest / Offset（v0.4.0） ----
        // VRCAimConstraint の RotationAtRest / RotationOffset は、ビルド時に
        // ActivateConstraint() を呼んでもキャプチャされない（v0.3.0 で実測）。
        // 手動セットアップでは、未ロックの Constraint がエディタ上で再計算を
        // 繰り返すことで値が決まる。そこで Editor 側のキャプチャ機能
        // （KemoLegAimCapture）が一時クローン上でその再計算を行わせ、結果を
        // ここへ保存する。ビルド時はこの値をそのまま設定する。
        // ターゲット位置やボーン割り当てを変更したら再キャプチャが必要。
        public Vector3 CapturedAtRestUpperLeft;
        public Vector3 CapturedAtRestLowerLeft;
        public Vector3 CapturedAtRestFootLeft;
        public Vector3 CapturedAtRestUpperRight;
        public Vector3 CapturedAtRestLowerRight;
        public Vector3 CapturedAtRestFootRight;
        public Vector3 CapturedOffsetUpperLeft;
        public Vector3 CapturedOffsetLowerLeft;
        public Vector3 CapturedOffsetFootLeft;
        public Vector3 CapturedOffsetUpperRight;
        public Vector3 CapturedOffsetLowerRight;
        public Vector3 CapturedOffsetFootRight;
        public bool OffsetsCaptured;

        // キャプチャ時点のターゲット位置スナップショット（陳腐化検出用）。
        public Vector3 CapturedTargetPositionUpperLeft;
        public Vector3 CapturedTargetPositionLowerLeft;
        public Vector3 CapturedTargetPositionFootLeft;
        public Vector3 CapturedTargetPositionUpperRight;
        public Vector3 CapturedTargetPositionLowerRight;
        public Vector3 CapturedTargetPositionFootRight;

        // ---- 生成されるターゲット名（仕様 §6） ----
        // コンポーネントのアタッチ時（Reset）および自動検出・再生成ボタンで生成される。
        public string TargetNameUpperLeft;
        public string TargetNameLowerLeft;
        public string TargetNameFootLeft;
        public string TargetNameUpperRight;
        public string TargetNameLowerRight;
        public string TargetNameFootRight;

        public LegChain GetTrackingChain(LegSide side) =>
            side == LegSide.Left ? TrackingLeft : TrackingRight;

        public LegChain GetDummyChain(LegSide side) =>
            side == LegSide.Left ? DummyLeft : DummyRight;

        public Vector3 GetTargetLocalPosition(LegSide side, LegPart part)
        {
            if (side == LegSide.Left)
            {
                switch (part)
                {
                    case LegPart.UpperLeg: return TargetPositionUpperLeft;
                    case LegPart.LowerLeg: return TargetPositionLowerLeft;
                    case LegPart.Foot: return TargetPositionFootLeft;
                }
            }
            else
            {
                switch (part)
                {
                    case LegPart.UpperLeg: return TargetPositionUpperRight;
                    case LegPart.LowerLeg: return TargetPositionLowerRight;
                    case LegPart.Foot: return TargetPositionFootRight;
                }
            }
            return Vector3.zero; // Toe は Aim Constraint の対象外
        }

        public void SetTargetLocalPosition(LegSide side, LegPart part, Vector3 localPosition)
        {
            if (side == LegSide.Left)
            {
                switch (part)
                {
                    case LegPart.UpperLeg: TargetPositionUpperLeft = localPosition; break;
                    case LegPart.LowerLeg: TargetPositionLowerLeft = localPosition; break;
                    case LegPart.Foot: TargetPositionFootLeft = localPosition; break;
                }
            }
            else
            {
                switch (part)
                {
                    case LegPart.UpperLeg: TargetPositionUpperRight = localPosition; break;
                    case LegPart.LowerLeg: TargetPositionLowerRight = localPosition; break;
                    case LegPart.Foot: TargetPositionFootRight = localPosition; break;
                }
            }
        }

        public Vector3 GetCapturedAtRest(LegSide side, LegPart part)
        {
            if (side == LegSide.Left)
            {
                switch (part)
                {
                    case LegPart.UpperLeg: return CapturedAtRestUpperLeft;
                    case LegPart.LowerLeg: return CapturedAtRestLowerLeft;
                    case LegPart.Foot: return CapturedAtRestFootLeft;
                }
            }
            else
            {
                switch (part)
                {
                    case LegPart.UpperLeg: return CapturedAtRestUpperRight;
                    case LegPart.LowerLeg: return CapturedAtRestLowerRight;
                    case LegPart.Foot: return CapturedAtRestFootRight;
                }
            }
            return Vector3.zero;
        }

        public Vector3 GetCapturedOffset(LegSide side, LegPart part)
        {
            if (side == LegSide.Left)
            {
                switch (part)
                {
                    case LegPart.UpperLeg: return CapturedOffsetUpperLeft;
                    case LegPart.LowerLeg: return CapturedOffsetLowerLeft;
                    case LegPart.Foot: return CapturedOffsetFootLeft;
                }
            }
            else
            {
                switch (part)
                {
                    case LegPart.UpperLeg: return CapturedOffsetUpperRight;
                    case LegPart.LowerLeg: return CapturedOffsetLowerRight;
                    case LegPart.Foot: return CapturedOffsetFootRight;
                }
            }
            return Vector3.zero;
        }

        public void SetCaptured(LegSide side, LegPart part, Vector3 atRest, Vector3 offset)
        {
            if (side == LegSide.Left)
            {
                switch (part)
                {
                    case LegPart.UpperLeg: CapturedAtRestUpperLeft = atRest; CapturedOffsetUpperLeft = offset; break;
                    case LegPart.LowerLeg: CapturedAtRestLowerLeft = atRest; CapturedOffsetLowerLeft = offset; break;
                    case LegPart.Foot: CapturedAtRestFootLeft = atRest; CapturedOffsetFootLeft = offset; break;
                }
            }
            else
            {
                switch (part)
                {
                    case LegPart.UpperLeg: CapturedAtRestUpperRight = atRest; CapturedOffsetUpperRight = offset; break;
                    case LegPart.LowerLeg: CapturedAtRestLowerRight = atRest; CapturedOffsetLowerRight = offset; break;
                    case LegPart.Foot: CapturedAtRestFootRight = atRest; CapturedOffsetFootRight = offset; break;
                }
            }
        }

        /// <summary>
        /// 現在のターゲット位置をキャプチャ時点のスナップショットとして保存する。
        /// キャプチャ完了時に Editor 側から呼ばれる。
        /// </summary>
        public void SnapshotTargetPositionsForCapture()
        {
            CapturedTargetPositionUpperLeft = TargetPositionUpperLeft;
            CapturedTargetPositionLowerLeft = TargetPositionLowerLeft;
            CapturedTargetPositionFootLeft = TargetPositionFootLeft;
            CapturedTargetPositionUpperRight = TargetPositionUpperRight;
            CapturedTargetPositionLowerRight = TargetPositionLowerRight;
            CapturedTargetPositionFootRight = TargetPositionFootRight;
        }

        /// <summary>
        /// 現在のターゲット位置がキャプチャ時点と一致するか。
        /// false の場合、キャプチャ済み At Rest / Offset は古い可能性がある。
        /// </summary>
        public bool CapturedPositionsMatch()
        {
            return CapturedTargetPositionUpperLeft == TargetPositionUpperLeft
                && CapturedTargetPositionLowerLeft == TargetPositionLowerLeft
                && CapturedTargetPositionFootLeft == TargetPositionFootLeft
                && CapturedTargetPositionUpperRight == TargetPositionUpperRight
                && CapturedTargetPositionLowerRight == TargetPositionLowerRight
                && CapturedTargetPositionFootRight == TargetPositionFootRight;
        }

        public string GetTargetName(LegSide side, LegPart part)
        {
            if (side == LegSide.Left)
            {
                switch (part)
                {
                    case LegPart.UpperLeg: return TargetNameUpperLeft;
                    case LegPart.LowerLeg: return TargetNameLowerLeft;
                    case LegPart.Foot: return TargetNameFootLeft;
                }
            }
            else
            {
                switch (part)
                {
                    case LegPart.UpperLeg: return TargetNameUpperRight;
                    case LegPart.LowerLeg: return TargetNameLowerRight;
                    case LegPart.Foot: return TargetNameFootRight;
                }
            }
            return null;
        }

        public void SetTargetName(LegSide side, LegPart part, string targetName)
        {
            if (side == LegSide.Left)
            {
                switch (part)
                {
                    case LegPart.UpperLeg: TargetNameUpperLeft = targetName; break;
                    case LegPart.LowerLeg: TargetNameLowerLeft = targetName; break;
                    case LegPart.Foot: TargetNameFootLeft = targetName; break;
                }
            }
            else
            {
                switch (part)
                {
                    case LegPart.UpperLeg: TargetNameUpperRight = targetName; break;
                    case LegPart.LowerLeg: TargetNameLowerRight = targetName; break;
                    case LegPart.Foot: TargetNameFootRight = targetName; break;
                }
            }
        }

        /// <summary>
        /// 現在のダミー側ボーン割り当てからターゲット名を再生成する。
        /// ボーンが未割り当ての部位は既存の名前を保持する。
        /// </summary>
        public void RegenerateTargetNames()
        {
            foreach (LegSide side in new[] { LegSide.Left, LegSide.Right })
            {
                var chain = GetDummyChain(side);
                foreach (var part in new[] { LegPart.UpperLeg, LegPart.LowerLeg, LegPart.Foot })
                {
                    var bone = chain.Get(part);
                    if (bone != null) SetTargetName(side, part, KemoLegTargetName.Generate(bone.name));
                }
            }
        }

        // コンポーネントがアタッチされた直後（および Reset 時）に Unity が呼ぶ。
        // この時点ではボーン割り当てが無いことが多いため、実際の名前生成は
        // 主に Editor 側の自動検出時／再生成ボタンで行われる。
        private void Reset()
        {
            RegenerateTargetNames();
        }
    }
}
