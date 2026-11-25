using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Paratap.KemoLegAim.Editor
{
    /// <summary>
    /// At Rest / Offset の事前キャプチャ（v0.4.0、v0.4.1 で診断強化）。
    ///
    /// 背景: VRCAimConstraint の RotationAtRest / RotationOffset は、手動セットアップ
    /// では「未ロック＋有効な Constraint がエディタ上で再計算を繰り返す」ことで
    /// 値が決まり、Activate（ロック）で固定される。ビルド時に生成した
    /// コンポーネントではその再計算が走らず、ActivateConstraint() もキャプチャを
    /// 行わないため、値は既定値 (0,0,0) のままになる（v0.2.0〜v0.3.0 で実測）。
    ///
    /// 本クラスは手動セットアップと同じ条件をエディタ上で再現する:
    /// アバターの一時クローンを作り、KemoLegAimBuilder.CreateAll でターゲットと
    /// 未ロックの Constraint を生成し、SDK のエディタ上 Constraint 実行に
    /// 再計算させ、確定した At Rest / Offset を元の KemoLegAimSetup へ保存する。
    /// クローンは読み取り後に破棄する。
    ///
    /// v0.4.0 では「再計算が起きて値が安定する」ことだけを待ったがタイムアウトした
    /// ため、v0.4.1 では以下を追加した:
    /// - 経過と現在値の定期ログ（再計算が起きているかどうかを Console で判別可能に）
    /// - 一時クローンを Hierarchy で選択し、Scene ビューの再描画も促す
    /// - 値が非ゼロのまま安定しない場合、および後半になっても (0,0,0) の場合に、
    ///   エディタ文脈での ActivateConstraint() を試す（Inspector の Activate ボタン
    ///   相当のキャプチャがエディタ上では働く可能性に賭ける）
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
        private static List<CreatedConstraint> _created;
        private static double _startTime;
        private static double _lastLogTime;
        private static double _firstNonZeroTime = -1;
        private static bool _activateTried;
        private static Dictionary<CreatedConstraint, Vector3> _lastOffsets;
        private static int _stableCount;

        private const double TimeoutSeconds = 30.0;
        private const double ActivateTrySeconds = 10.0;

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

            _created = KemoLegAimBuilder.CreateAll(cloneSetup);
            if (_created.Count == 0)
            {
                Fail("Constraint を生成できませんでした（ボーン割り当て不足）。Console の警告を確認してください。");
                return;
            }

            _sourceSetup = setup;
            _lastOffsets = new Dictionary<CreatedConstraint, Vector3>();
            _stableCount = 0;
            _firstNonZeroTime = -1;
            _activateTried = false;
            _startTime = EditorApplication.timeSinceStartup;
            _lastLogTime = _startTime;
            IsRunning = true;
            LastMessage = "キャプチャ中…（一時クローン上で Constraint の再計算を待っています。Console に経過が出ます）";
            Debug.Log($"[KemoLegAim] キャプチャ開始: Constraint {_created.Count} 個を一時クローンに生成しました。", setup);
            EditorApplication.update += OnEditorUpdate;
        }

        private static void OnEditorUpdate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Fail("Play モードへ移行したためキャプチャを中止しました。");
                return;
            }
            if (_clone == null || _created == null)
            {
                Fail("一時クローンが失われたためキャプチャを中止しました。");
                return;
            }

            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();

            double now = EditorApplication.timeSinceStartup;
            double elapsed = now - _startTime;

            bool allNonZero = true;
            bool stable = true;
            foreach (var c in _created)
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

            if (allNonZero && _firstNonZeroTime < 0) _firstNonZeroTime = now;

            // 定期ログ（再計算が起きているかの切り分け用）
            if (now - _lastLogTime >= 5.0)
            {
                _lastLogTime = now;
                Debug.Log($"[KemoLegAim] キャプチャ経過 {elapsed:F0} 秒: {DescribeOffsets()}");
            }

            if (allNonZero && stable)
            {
                _stableCount++;
                if (_stableCount >= 3)
                {
                    Succeed();
                    return;
                }
            }
            else
            {
                _stableCount = 0;
            }

            // ActivateConstraint のエディタ文脈キャプチャを試す条件:
            // - 値が非ゼロになったのに 5 秒安定しない（再計算は起きているが揺れる）
            // - 開始から ActivateTrySeconds 経っても (0,0,0) のまま（受動的な再計算が起きない）
            if (!_activateTried &&
                ((_firstNonZeroTime > 0 && now - _firstNonZeroTime > 5.0) ||
                 elapsed > ActivateTrySeconds))
            {
                _activateTried = true;
                Debug.Log("[KemoLegAim] ActivateConstraint によるキャプチャを試します。");
                foreach (var c in _created)
                {
                    c.Constraint.ApplyConfigurationChanges();
                    c.Constraint.ActivateConstraint();
                }
                // ロック後は値が固定されるため、次の安定判定で確定できる
                _lastOffsets.Clear();
                _stableCount = 0;
            }

            if (elapsed > TimeoutSeconds)
            {
                Fail($"タイムアウト（{TimeoutSeconds:F0} 秒）。最後の Offset 値: {DescribeOffsets()} " +
                     "SDK Control Panel の「Execute VRChat Constraints in Edit Mode」が有効か、" +
                     "この経過ログと合わせて確認してください。");
            }
        }

        private static string DescribeOffsets()
        {
            var sb = new StringBuilder();
            foreach (var c in _created)
            {
                if (sb.Length > 0) sb.Append(" / ");
                sb.Append($"{BoneAutoDetector.SideLabel(c.Side)} {BoneAutoDetector.PartLabel(c.Part)}: {c.Constraint.RotationOffset}");
            }
            return sb.ToString();
        }

        private static void Succeed()
        {
            Undo.RecordObject(_sourceSetup, "KemoLeg capture At Rest / Offset");
            foreach (var c in _created)
            {
                _sourceSetup.SetCaptured(c.Side, c.Part,
                    c.Constraint.RotationAtRest, c.Constraint.RotationOffset);
                Debug.Log(
                    $"[KemoLegAim] キャプチャ: {BoneAutoDetector.SideLabel(c.Side)} {BoneAutoDetector.PartLabel(c.Part)} " +
                    $"At Rest = {c.Constraint.RotationAtRest}, Offset = {c.Constraint.RotationOffset}",
                    _sourceSetup);
            }
            _sourceSetup.SnapshotTargetPositionsForCapture();
            _sourceSetup.OffsetsCaptured = true;
            EditorUtility.SetDirty(_sourceSetup);

            LastSucceeded = true;
            LastMessage = $"キャプチャ完了（{_created.Count} 個）。ビルド時はこの値が使われます。" +
                          "ターゲット位置を変更したら再キャプチャしてください。";
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
            _created = null;
            _sourceSetup = null;
            _lastOffsets = null;
            _stableCount = 0;
            _firstNonZeroTime = -1;
            _activateTried = false;
        }
    }
}
