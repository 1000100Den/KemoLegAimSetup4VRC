using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Paratap.KemoLegAim.Editor
{
    /// <summary>
    /// KemoLegAimSetup の CustomEditor（仕様 §5 / §8、v1.1 付記A / B）。
    /// - アタッチ直後（OnEnable で未割り当てを検知した場合）に自動検出を実行
    /// - ボーンフィールド、自動検出ボタン、名前再生成ボタン
    /// - ターゲット6個のローカル Position (X, Y, Z) 数値編集と「初期値を再計算」ボタン
    ///   （旧割合スライダーは v0.2.0 で廃止）
    /// - Humanoid チェック結果の警告ボックス＋一覧（各行に「選択」ボタン、再チェックボタン）
    /// - Scene ビューでのギズモ（各ターゲットのワールド位置にハンドルを表示し、
    ///   ドラッグ結果を親ローカル Position へ逆算して反映）
    /// </summary>
    [CustomEditor(typeof(KemoLegAimSetup))]
    public class KemoLegAimSetupEditor : UnityEditor.Editor
    {
        private KemoLegAimSetup _setup;
        private List<HumanoidCheckIssue> _issues = new List<HumanoidCheckIssue>();
        private List<string> _detectWarnings = new List<string>();

        private void OnEnable()
        {
            _setup = target as KemoLegAimSetup;
            if (_setup == null) return;

            // アタッチ直後など、未割り当てなら自動検出する（仕様 §5）
            if (_setup.TrackingLeft.IsEmpty && _setup.TrackingRight.IsEmpty &&
                _setup.DummyLeft.IsEmpty && _setup.DummyRight.IsEmpty)
            {
                Undo.RecordObject(_setup, "KemoLeg auto-detect");
                var result = BoneAutoDetector.Detect(_setup, overwrite: false);
                _detectWarnings = result.Warnings;
                _setup.RegenerateTargetNames();
                EditorUtility.SetDirty(_setup);
            }

            RunCheck();
        }

        private void RunCheck()
        {
            _issues = _setup != null ? HumanoidCheck.Run(_setup) : new List<HumanoidCheckIssue>();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("ボーン割り当て", EditorStyles.boldLabel);
            DrawBoneField("Hips", "Hips");
            DrawChainFields("トラッキング側（左）", "TrackingLeft");
            DrawChainFields("トラッキング側（右）", "TrackingRight");
            DrawChainFields("ダミー側（左）", "DummyLeft");
            DrawChainFields("ダミー側（右）", "DummyRight");

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("自動検出"))
                {
                    Undo.RecordObject(_setup, "KemoLeg auto-detect");
                    var result = BoneAutoDetector.Detect(_setup, overwrite: true);
                    _detectWarnings = result.Warnings;
                    _setup.RegenerateTargetNames();
                    EditorUtility.SetDirty(_setup);
                    RunCheck();
                }
                if (GUILayout.Button("ターゲット名を再生成"))
                {
                    Undo.RecordObject(_setup, "KemoLeg regenerate target names");
                    _setup.RegenerateTargetNames();
                    EditorUtility.SetDirty(_setup);
                }
            }
            foreach (var warning in _detectWarnings)
            {
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("ターゲット位置（ローカル Position）", EditorStyles.boldLabel);
            if (!_setup.TargetPositionsInitialized)
            {
                EditorGUILayout.HelpBox(
                    "ターゲット位置の初期値がまだ生成されていません。自動検出を実行するか、「初期値を再計算」を押してください。",
                    MessageType.Warning);
            }
            DrawTargetPositionField(LegSide.Left, LegPart.UpperLeg, "TargetPositionUpperLeft");
            DrawTargetPositionField(LegSide.Left, LegPart.LowerLeg, "TargetPositionLowerLeft");
            DrawTargetPositionField(LegSide.Left, LegPart.Foot, "TargetPositionFootLeft");
            DrawTargetPositionField(LegSide.Right, LegPart.UpperLeg, "TargetPositionUpperRight");
            DrawTargetPositionField(LegSide.Right, LegPart.LowerLeg, "TargetPositionLowerRight");
            DrawTargetPositionField(LegSide.Right, LegPart.Foot, "TargetPositionFootRight");
            if (GUILayout.Button("初期値を再計算"))
            {
                Undo.RecordObject(_setup, "KemoLeg recalculate target positions");
                var animator = BoneAutoDetector.FindAnimator(_setup);
                Transform root = animator != null ? animator.transform : _setup.transform;
                TargetPlacement.InitializeTargetPositions(_setup, root, force: true);
                EditorUtility.SetDirty(_setup);
            }
            EditorGUILayout.HelpBox(
                "親ボーン（Upper→LowerLeg / Lower・Foot→Foot）に対するローカル Position です。" +
                "Play 中にターゲット自身の Inspector に表示される Position と同じ値です。" +
                "Scene ビューのハンドルをドラッグしても編集できます。",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("ターゲット名", EditorStyles.boldLabel);
            DrawTextField("TargetNameUpperLeft", "左 UpperLeg");
            DrawTextField("TargetNameLowerLeft", "左 LowerLeg");
            DrawTextField("TargetNameFootLeft", "左 Foot");
            DrawTextField("TargetNameUpperRight", "右 UpperLeg");
            DrawTextField("TargetNameLowerRight", "右 LowerLeg");
            DrawTextField("TargetNameFootRight", "右 Foot");

            EditorGUILayout.Space();
            DrawCheckResults();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawBoneField(string propertyPath, string label)
        {
            var prop = serializedObject.FindProperty(propertyPath);
            if (prop != null) EditorGUILayout.PropertyField(prop, new GUIContent(label));
        }

        private void DrawChainFields(string header, string chainProperty)
        {
            EditorGUILayout.LabelField(header, EditorStyles.miniBoldLabel);
            DrawBoneField(chainProperty + ".UpperLeg", "UpperLeg");
            DrawBoneField(chainProperty + ".LowerLeg", "LowerLeg");
            DrawBoneField(chainProperty + ".Foot", "Foot");
            DrawBoneField(chainProperty + ".Toe", "Toe");
        }

        private void DrawTargetPositionField(LegSide side, LegPart part, string propertyPath)
        {
            var prop = serializedObject.FindProperty(propertyPath);
            if (prop == null) return;
            // ラベルはターゲット名（未生成なら部位名で代用）
            string name = _setup.GetTargetName(side, part);
            string label = !string.IsNullOrEmpty(name)
                ? name
                : $"{BoneAutoDetector.SideLabel(side)} {BoneAutoDetector.PartLabel(part)}";
            EditorGUILayout.PropertyField(prop, new GUIContent(label));
        }

        private void DrawTextField(string propertyPath, string label)
        {
            var prop = serializedObject.FindProperty(propertyPath);
            if (prop != null) EditorGUILayout.PropertyField(prop, new GUIContent(label));
        }

        // ---- Humanoid チェック結果（仕様 §8） ----
        private void DrawCheckResults()
        {
            EditorGUILayout.LabelField("Humanoid 割り当てチェック", EditorStyles.boldLabel);

            if (_issues.Count == 0)
            {
                EditorGUILayout.HelpBox("警告はありません。", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"Humanoid割り当てチェック: {_issues.Count} 件の警告（ビルドは続行されます）",
                    MessageType.Warning);

                foreach (var issue in _issues)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("・" + issue.Message);
                        if (issue.Bone != null && GUILayout.Button("選択", GUILayout.Width(48)))
                        {
                            Selection.activeGameObject = issue.Bone.gameObject;
                            EditorGUIUtility.PingObject(issue.Bone.gameObject);
                        }
                    }
                }
            }

            if (GUILayout.Button("再チェック"))
            {
                RunCheck();
            }
        }

        // ---- Scene ビュー ギズモ（仕様 v1.1 付記B） ----
        // ハンドル位置 = 親ボーンの localToWorldMatrix × ローカル Position。
        // ドラッグされたら親ローカルへ逆算してフィールドへ反映する。
        private void OnSceneGUI()
        {
            if (_setup == null) return;

            foreach (var side in new[] { LegSide.Left, LegSide.Right })
            {
                var tracking = _setup.GetTrackingChain(side);
                foreach (var part in new[] { LegPart.UpperLeg, LegPart.LowerLeg, LegPart.Foot })
                {
                    // 親は「対応するボーン」ではなく1つ下の関節側（付記A）
                    var parent = TargetPlacement.GetParentBone(tracking, part);
                    if (parent == null) continue;

                    Vector3 local = _setup.GetTargetLocalPosition(side, part);
                    Vector3 pos = TargetPlacement.ParentLocalToWorld(parent, local);

                    Handles.color = Color.cyan;
                    Handles.DrawLine(parent.position, pos);
                    Handles.SphereHandleCap(0, pos, Quaternion.identity,
                        HandleUtility.GetHandleSize(pos) * 0.05f, EventType.Repaint);

                    string name = _setup.GetTargetName(side, part);
                    Handles.Label(pos, string.IsNullOrEmpty(name) ? part.ToString() : name);

                    EditorGUI.BeginChangeCheck();
                    Vector3 newPos = Handles.PositionHandle(pos, Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_setup, "KemoLeg move target");
                        _setup.SetTargetLocalPosition(side, part,
                            TargetPlacement.WorldToParentLocal(parent, newPos));
                        // Inspector 側の数値フィールドにも即時反映させる
                        serializedObject.Update();
                        EditorUtility.SetDirty(_setup);
                    }
                }
            }
        }
    }
}
