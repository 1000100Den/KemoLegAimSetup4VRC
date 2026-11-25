using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using UnityEngine;

using Paratap.KemoLegAim.Editor;

[assembly: ExportsPlugin(typeof(KemoLegAimPlugin))]

namespace Paratap.KemoLegAim.Editor
{
    /// <summary>
    /// NDMF プラグイン（仕様 §3 / §8 / §9）。
    /// - Resolving: Humanoid 割り当てチェックを行い、警告を NDMF のエラーレポート
    ///   （ErrorSeverity.NonFatal、対象ボーン参照付き）で報告する。ビルドは続行される。
    /// - Transforming: クローン側へターゲット生成と VRCAimConstraint 適用を行い、
    ///   処理後に設定用コンポーネントを除去する。
    /// </summary>
    public class KemoLegAimPlugin : Plugin<KemoLegAimPlugin>
    {
        public override string QualifiedName => "jp.paratap.kemoleg-aim";
        public override string DisplayName => "KemoLeg Aim Setup";

        // 警告文面のローカライザ。アセットを使わず、コード内の辞書から引く
        // （NDMF / Modular Avatar の Localizer 利用パターンに倣ったもの）。
        internal static readonly Localizer Loc = CreateLocalizer();

        private static Localizer CreateLocalizer()
        {
            var ja = new Dictionary<string, string>
            {
                ["KemoLegAim.Humanoid.TrackingMissing"] =
                    "トラッキング側 {1} が Humanoid に割り当てられていません。",
                ["KemoLegAim.Humanoid.TrackingMismatch"] =
                    "トラッキング側 {1} のフィールドが Humanoid 割り当てと一致しません。",
                ["KemoLegAim.Humanoid.DummyAssigned"] =
                    "ボーン {0} が Humanoid の {1} に割り当てられています。ダミー側ボーンの可能性があります。",
                ["KemoLegAim.Humanoid.DummyLikeName"] =
                    "Humanoid の {1} にダミー候補名のボーン {0} が割り当てられています。",
                ["KemoLegAim.Humanoid.Generic"] =
                    "Humanoid 割り当てチェックで警告がありました: {1}",
            };
            var en = new Dictionary<string, string>
            {
                ["KemoLegAim.Humanoid.TrackingMissing"] =
                    "Tracking bone {1} is not assigned in the Humanoid rig.",
                ["KemoLegAim.Humanoid.TrackingMismatch"] =
                    "Tracking field for {1} does not match the Humanoid rig assignment.",
                ["KemoLegAim.Humanoid.DummyAssigned"] =
                    "Bone {0} is assigned to Humanoid bone {1}; it may be a dummy-side bone.",
                ["KemoLegAim.Humanoid.DummyLikeName"] =
                    "A dummy-like bone {0} is assigned to Humanoid bone {1}.",
                ["KemoLegAim.Humanoid.Generic"] =
                    "Humanoid assignment check warning: {1}",
            };

            return new Localizer("ja-JP", () => new List<(string, Func<string, string>)>
            {
                ("ja-JP", key => ja.TryGetValue(key, out var v) ? v : null),
                ("en-US", key => en.TryGetValue(key, out var v) ? v : null),
            });
        }

        protected override void Configure()
        {
            InPhase(BuildPhase.Resolving)
                .Run("KemoLeg Humanoid check", ctx =>
                {
                    foreach (var setup in ctx.AvatarRootObject.GetComponentsInChildren<KemoLegAimSetup>(true))
                    {
                        ReportHumanoidIssues(setup);
                    }
                });

            InPhase(BuildPhase.Transforming)
                .Run("Apply KemoLeg Aim constraints", ctx =>
                {
                    foreach (var setup in ctx.AvatarRootObject.GetComponentsInChildren<KemoLegAimSetup>(true))
                    {
                        KemoLegAimBuilder.Apply(setup);
                        // 適用済みの設定用コンポーネントは除去する
                        // （IEditorOnly のため最終的には残らないが、NDMF の流儀に合わせて明示的に消す）。
                        UnityEngine.Object.DestroyImmediate(setup);
                    }
                });
        }

        private static void ReportHumanoidIssues(KemoLegAimSetup setup)
        {
            var issues = HumanoidCheck.Run(setup);
            foreach (var issue in issues)
            {
                // 仕様 §8 の表示先2: Unity Console への LogWarning
                // （ErrorReport 経由でもログには出るが、自前表示として明示的に残す）
                Debug.LogWarning($"[KemoLegAim] {issue.Message}", issue.Bone != null ? (UnityEngine.Object)issue.Bone : setup);

                // NDMF Console への警告レポート連携（仕様 §9）。
                // NonFatal なのでビルドはブロックされない。
                // 引数に Unity オブジェクトを渡すと参照として紐づけられる。
                switch (issue.Kind)
                {
                    case HumanoidCheckIssueKind.TrackingMissing:
                        ErrorReport.ReportError(Loc, ErrorSeverity.NonFatal,
                            "KemoLegAim.Humanoid.TrackingMissing",
                            setup, issue.PartLabel);
                        break;
                    case HumanoidCheckIssueKind.TrackingMismatch:
                        ErrorReport.ReportError(Loc, ErrorSeverity.NonFatal,
                            "KemoLegAim.Humanoid.TrackingMismatch",
                            issue.Bone, issue.PartLabel);
                        break;
                    case HumanoidCheckIssueKind.DummyAssignedToHumanoid:
                        ErrorReport.ReportError(Loc, ErrorSeverity.NonFatal,
                            "KemoLegAim.Humanoid.DummyAssigned",
                            issue.Bone, issue.HumanoidBoneName);
                        break;
                    case HumanoidCheckIssueKind.DummyLikeNameInHumanoid:
                        ErrorReport.ReportError(Loc, ErrorSeverity.NonFatal,
                            "KemoLegAim.Humanoid.DummyLikeName",
                            issue.Bone, issue.HumanoidBoneName);
                        break;
                    default:
                        ErrorReport.ReportError(Loc, ErrorSeverity.NonFatal,
                            "KemoLegAim.Humanoid.Generic",
                            issue.Bone != null ? (UnityEngine.Object)issue.Bone : setup, issue.Message);
                        break;
                }
            }
        }
    }
}
