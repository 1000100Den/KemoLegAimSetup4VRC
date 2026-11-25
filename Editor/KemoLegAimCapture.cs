using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Paratap.KemoLegAim.Editor
{
    /// <summary>
    /// At Rest / Offset の事前キャプチャ（v0.4.0、v0.5.0 で段階式に変更）。
    ///
    /// 背景: VRCAimConstraint の RotationAtRest / RotationOffset は、手動セットアップ
    /// では「未ロック＋有効な Constraint がエディタ上で再計算を繰り返す」ことで
    /// 値が決まり、Activate（ロック）で固定される。ビルド時に生成した
    /// コンポーネントではその再計算が走らず、ActivateConstraint() もキャプチャを
    /// 行わないため、値は既定値 (0,0,0) のままになる（v0.2.0〜v0.3.0 で実測）。
    ///
    /// v0.4.1 の一斉キャプチャでは UpperLeg は手動値と一致したが、LowerLeg 以下が
    /// 一致しなかった（2026-11-26 実測）。原因は、手動セットアップでは上位の
    /// Constraint が有効化され脚を駆動した状態で下位を設定するのに対し、
    /// 一斉キャプチャでは全て未駆動の状態で値を取っていたこと。
    ///
    /// v0.5.0 では手動セットアップの手順をそのまま再現する段階式にした:
    /// 一時クローン上に全 Constraint を未ロックで生成し、
    /// UpperLeg → LowerLeg → Foot の順に「再計算が安定するまで待つ → 値を読み、
    /// その Constraint をロック（駆動開始）」を繰り返す。
    /// 下位の再計算は上位が駆動した状態で行われるため、手動値と一致する。
    /// 確定値は元の KemoLegAimSetup へ保存し、クローンは破棄する。
    ///
    /// 前提: SDK Control Panel の「Execute VRChat Constraints in Edit Mode」が
    /// 有効であること。
    /// </summary>
    public static class KemoLegAimCapture
    {
        public static bool IsRunning { get; private set; }
        public static string LastMessage { get; private set; }
        public static bool LastSucceeded { get; private set; }

        private static KemoLegAimSetup _sourceSetup;
        private static GameObject _clone;
        private static List<List<CreatedConstraint>> _stages;
        private static int _stageIndex;
        private static double _stageStartTime;
        private static double _lastLogTime;
        private static bool _activateTriedInStage;
        private static Dictionary<CreatedConstraint, Vector3> _lastOffsets;
        private static int _stableCount;
        private static readonly Dictionary<(LegSide, LegPart), (Vector3 AtRest, Vector3 Offset)> _results =
            new Dictionary<(LegSide, LegPart), (Vector3, Vector3)>();

        private const double StageTimeoutSeconds = 30.0;
        private const double ActivateTrySeconds = 12.0;
        private const double MinStageSeconds = 2.0;

        public static void Begin(KemoLegAimSetup setup)
        {
            if (IsRunning)
            {
                LastMessage = "キャプチャは既に実行中です。";
                return;
            }
            if (setup == null) return;

            if (!setup.TargetPositionsInitialized)
            {
                LastSucceeded = false;
                LastMessage = "ターゲット位置の初期値が未生成のためキャプチャできません。" +
                              "先に自動検出または「初期値を再計算」を実行してください。";
                Debug.LogWarning("[KemoLegAim] " + LastMessage, setup);
                return;
            }

            var animator = BoneAutoDetector.FindAnimator(setup);
            if (animator == null)
            {
                LastSucceeded = false;
                LastMessage = "Animator が見つからないためキャプチャできません。";
                Debug.LogWarning("[KemoLegAim] " + LastMessage, setup);
                return;
            }

            var avatarRoot = animator.gameObject;

            _clone = Object.Instantiate(avatarRoot);
            _clone.name = avatarRoot.name + " (KemoLegAim Capture)";
            // エディタ側の Constraint 処理が選択中のアバターを対象とする場合に備える
            Selection.activeGameObject = _clone;

            var cloneSetup = _clone.GetComponentInChildren<KemoLegAimSetup>();
            if (cloneSetup == null)
            {
                Fail("クローン側に KemoLegAimSetup が見つかりませんでした。");
                return;
            }

            var created = KemoLegAimBuilder.CreateAll(cloneSetup);
            if (created.Count == 0)
            {
                Fail("Constraint を生成できませんでした（ボーン割り当て不足）。Console の警告を確認してください。");
                return;
            }

            // 段階分け: Upper → Lower → Foot（左右は同段階）
            _stages = new List<List<CreatedConstraint>>();
            foreach (var part in new[] { LegPart.UpperLeg, LegPart.LowerLeg, LegPart.Foot })
            {
                var stage = created.FindAll(c => c.Part == part);
                if (stage.Count > 0) _stages.Add(stage);
            }

            _sourceSetup = setup;
            _results.Clear();
            _stageIndex = 0;
            _lastOffsets = new Dictionary<CreatedConstraint, Vector3>();
            _stableCount = 0;
            _activateTriedInStage = false;
            _stageStartTime = EditorApplication.timeSinceStartup;
            _lastLogTime = _stageStartTime;
            IsRunning = true;
            LastMessage = "キャプチャ中…（UpperLeg から順に再計算を待っています。Console に経過が出ます）";
            Debug.Log($"[KemoLegAim] キャプチャ開始（段階式）: Constraint {created.Count} 個を一時クローンに生成しました。", setup);
            EditorApplication.update += OnEditorUpdate;
        }

        private static List<CreatedConstraint> CurrentStage =>
            _stages != null && _stageIndex < _stages.Count ? _stages[_stageIndex] : null;

        private static void OnEditorUpdate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Fail("Play モードへ移行したためキャプチャを中止しました。");
                return;
            }
            if (_clone == null || CurrentStage == null)
            {
                Fail("一時クローンが失われたためキャプチャを中止しました。");
                return;
            }

            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();

            double now = EditorApplication.timeSinceStartup;
            double stageElapsed = now - _stageStartTime;
            var stage = CurrentStage;

            bool allNonZero = true;
            bool stable = true;
            foreach (var c in stage)
            {
                var offset = c.Constraint.RotationOffset;
                if (offset == Vector3.zero) allNonZero = false;
                if (_lastOffsets.TryGetValue(c, out var prev) &&
                    (prev - offset).sqrMagnitude > 1e-8f)
                {
                    stable = false;
                }
                _lastOffsets[c] = offset;
            }

            if (now - _lastLogTime >= 5.0)
            {
                _lastLogTime = now;
                Debug.Log($"[KemoLegAim] キャプチャ経過: 段階 {_stageIndex + 1}/{_stages.Count} " +
                          $"({BoneAutoDetector.PartLabel(stage[0].Part)}) {stageElapsed:F0} 秒: {DescribeOffsets(stage)}");
            }

            // 上位段階をロックした直後は、駆動が始まる前に下位の旧値が
            // 「安定して見える」ことがあるため、段階開始から一定時間は確定しない。
            if (allNonZero && stable && stageElapsed > MinStageSeconds)
            {
                _stableCount++;
                if (_stableCount >= 3)
                {
                    CompleteStage(now);
                    return;
                }
            }
            else
            {
                _stableCount = 0;
            }

            // 受動的な再計算が起きない場合の保険: エディタ文脈での Activate を試す。
            // ただしロック後に値が (0,0,0) のままなら、その段階は失敗として扱う
            // （CompleteStage は allNonZero を要求するため、ゼロのまま確定はしない）。
            if (!_activateTriedInStage && stageElapsed > ActivateTrySeconds)
            {
                _activateTriedInStage = true;
                Debug.Log($"[KemoLegAim] 段階 {_stageIndex + 1}: ActivateConstraint によるキャプチャを試します。");
                foreach (var c in stage)
                {
                    c.Constraint.ApplyConfigurationChanges();
                    c.Constraint.ActivateConstraint();
                }
                _lastOffsets.Clear();
                _stableCount = 0;
            }

            if (stageElapsed > StageTimeoutSeconds)
            {
                Fail($"タイムアウト: 段階 {_stageIndex + 1}（{BoneAutoDetector.PartLabel(stage[0].Part)}）で" +
                     $"再計算が確認できませんでした。最後の Offset 値: {DescribeOffsets(stage)} " +
                     "SDK Control Panel の「Execute VRChat Constraints in Edit Mode」が有効か、" +
                     "この経過ログと合わせて確認してください。");
            }
        }

        /// <summary>
        /// 現段階の値を読み取り、その Constraint 群をロックして駆動を開始する。
        /// 次段階の再計算は、この駆動状態の下で行われる（手動セットアップと同じ条件）。
        /// </summary>
        private static void CompleteStage(double now)
        {
            var stage = CurrentStage;
            foreach (var c in stage)
            {
                _results[(c.Side, c.Part)] = (c.Constraint.RotationAtRest, c.Constraint.RotationOffset);
                Debug.Log(
                    $"[KemoLegAim] 段階 {_stageIndex + 1} 確定: {BoneAutoDetector.SideLabel(c.Side)} {BoneAutoDetector.PartLabel(c.Part)} " +
                    $"At Rest = {c.Constraint.RotationAtRest}, Offset = {c.Constraint.RotationOffset}");
                if (!_activateTriedInStage)
                {
                    c.Constraint.ApplyConfigurationChanges();
                    c.Constraint.ActivateConstraint();
                }
            }

            _stageIndex++;
            if (_stageIndex >= _stages.Count)
            {
                Succeed();
                return;
            }

            // 次段階の追跡をリセット（上位が駆動し始めたことで値は動き出す）
            _lastOffsets.Clear();
            _stableCount = 0;
            _activateTriedInStage = false;
            _stageStartTime = now;
            _lastLogTime = now;
        }

        private static string DescribeOffsets(List<CreatedConstraint> stage)
        {
            var sb = new StringBuilder();
            foreach (var c in stage)
            {
                if (sb.Length > 0) sb.Append(" / ");
                sb.Append($"{BoneAutoDetector.SideLabel(c.Side)} {BoneAutoDetector.PartLabel(c.Part)}: {c.Constraint.RotationOffset}");
            }
            return sb.ToString();
        }

        private static void Succeed()
        {
            Undo.RecordObject(_sourceSetup, "KemoLeg capture At Rest / Offset");
            foreach (var kv in _results)
            {
                _sourceSetup.SetCaptured(kv.Key.Item1, kv.Key.Item2, kv.Value.AtRest, kv.Value.Offset);
            }
            _sourceSetup.SnapshotTargetPositionsForCapture();
            _sourceSetup.OffsetsCaptured = true;
            EditorUtility.SetDirty(_sourceSetup);

            LastSucceeded = true;
            LastMessage = $"キャプチャ完了（{_results.Count} 個）。ビルド時はこの値が使われます。" +
                          "ターゲット位置を変更したら再キャプチャしてください。";
            Debug.Log("[KemoLegAim] " + LastMessage, _sourceSetup);
            Cleanup();
        }

        private static void Fail(string message)
        {
            LastSucceeded = false;
            LastMessage = message;
            Debug.LogWarning("[KemoLegAim] キャプチャ失敗: " + message);
            Cleanup();
        }

        private static void Cleanup()
        {
            EditorApplication.update -= OnEditorUpdate;
            IsRunning = false;
            if (_clone != null)
            {
                Object.DestroyImmediate(_clone);
                _clone = null;
            }
            _stages = null;
            _sourceSetup = null;
            _lastOffsets = null;
            _stableCount = 0;
            _activateTriedInStage = false;
        }
    }
}
