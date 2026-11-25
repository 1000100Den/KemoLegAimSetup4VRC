# KemoLeg Aim Setup（試作）

VRChatアバター向けに、フルボディトラッキング時に踵が動き、爪先／足先が
トラッキングと一致する鳥脚（獣脚）型の足を VRC Aim Constraint で実装する手順を、
Modular Avatar / NDMF 前提で非破壊に自動化する Unity エディタ拡張の試作パッケージです。

- 仕様: `../spec-v1.1.md`（確定 v1.1、付記A/B/C を含む）
- API調査: `../api-research.md`
- 対象: Unity 2022.3.22f1 / Unity 6 (6000.0.67f1)
- 正式サポートする VRC SDK の下限: 3.10.6（Unity 6 側は SDK 3.11.0-beta.1 前提）

## 導入方法

前提パッケージ:

- VRChat SDK (Avatars) >= 3.10.6
- NDMF (nadena.dev.ndmf) >= 1.10.0, < 2.0.0
  （Modular Avatar 導入済みの環境では NDMF は自動で入っています）

導入手順（いずれか）:

1. **VPM / VCC**: このフォルダを VPM ローカルパッケージとしてプロジェクトに追加する。
2. **フォルダ配置**: このフォルダ一式をプロジェクトの `Packages/jp.paratap.kemoleg-aim/`
   にコピーする（埋め込みパッケージとして認識されます）。

## 使い方

事前準備（ユーザーが行うこと）:

- Blender 側の下準備（ダミーボーン作成・ウェイト・ロールクリア）は本パッケージの対象外です。
- FBX のインポート設定（Humanoid 化、Mapping の割り当て）は Unity の通常手順で行ってください。
  本パッケージはインポート設定を変更しません。

セットアップ:

1. シーンに配置したアバターの任意の子オブジェクト（通常はアバターベー）に
   `KemoLeg Aim Setup` コンポーネントを追加する
   （Add Component → KemoLeg Aim → KemoLeg Aim Setup）。
2. アタッチ時にボーン自動検出が実行され、トラッキング側（Humanoid 割り当てから取得）と
   ダミー側（名前・チェーン構造からの推定）がフィールドに割り当てられます。
   ズレている場合は Inspector で手動修正でき、「自動検出」ボタンで再実行できます。
3. Inspector の Humanoid 割り当てチェックで警告がないか確認します
   （警告があってもビルドは続行されます）。
4. ターゲット位置を調整します。Inspector の「ターゲット位置（ローカル Position）」で、
   ターゲット6個それぞれの Position (X, Y, Z) を数値編集できます（範囲制限なし）。
   - 基準は親ボーンに対するローカル Position で、Play 中にターゲット自身の
     Inspector に表示される Position と同じ値です。
   - 親ボーンの対応は次のとおりです（手動セットアップの階層と同じ）。

     | ターゲット | 親（トラッキング側） |
     |---|---|
     | AIMTARGET_…_UPPER_LEG_x | LowerLeg_x |
     | AIMTARGET_…_LOWER_LEG_x | Foot_x |
     | AIMTARGET_…_FOOT_x | Foot_x |

   - 初期値は自動検出時／アタッチ時に自動生成されます。生成方法は、旧割合方式の
     ワールド目標位置（対応トラッキングボーン位置＋アバター前方×割合×セグメント長、
     割合は Upper +25% / Lower -25% / Foot +25%）を、親ボーンのローカル座標へ
     換算したものです。
   - 初期化済みの値は自動検出を再実行しても上書きされません。初期値に戻したい
     場合は「初期値を再計算」ボタンを押してください。
   - Scene ビューでは各ターゲットの位置にハンドルが表示され、ドラッグすると
     親ローカル Position へ逆算されてフィールドに反映されます。
5. そのままアバターをビルド／アップロードします。NDMF プラグインがビルド時に
   クローン側へ以下を適用し、元のアバターは変更されません。
   - ターゲット用空オブジェクト（例: `AIMTARGET_DUMMY_UPPER_LEG_L`）を生成し、
     上表の親ボーンの子に、フィールドのローカル Position そのままで配置
   - ダミー側 UpperLeg / LowerLeg / Foot（左右計6ボーン）に VRC Aim Constraint を付与
     （World Up = Object Up、World Up Object = Hips、Source = 対応ターゲット、有効化）

## 仕様との対応

| 仕様項目 | 実装 |
|---|---|
| §3 非破壊方式 | 設定用コンポーネント `KemoLegAimSetup`（Runtime）＋ NDMF プラグイン（Editor）。Transforming フェーズでクローン側にのみ適用 |
| §5 ボーン自動検出 | `Editor/BoneAutoDetector.cs`。トラッキング側は `Animator.GetBoneTransform`、ダミー側は識別語 A群（重み高）／B群（重み中）＋部位語＋左右＋チェーン＋近傍性のスコアリング |
| §6 ターゲット命名 | `Runtime/KemoLegTargetName.cs`。`AIMTARGET_` 接頭辞＋UPPER_SNAKE 変換。アタッチ時・検出時・ボタンで生成／再生成 |
| v1.1 付記A ターゲット階層 | 親は1つ下の関節側（Upper→LowerLeg、Lower・Foot→Foot）。対応付けは `Editor/TargetPlacement.cs` の `ParentPart` / `GetParentBone` に集約 |
| v1.1 付記B 位置編集方式 | 割合スライダーを廃止し、ターゲット6個のローカル Position (X, Y, Z) 数値編集に変更。初期値は旧割合方式のワールド目標位置から親ローカルへ換算して自動生成（再計算ボタンあり）。ビルド時はフィールド値をそのまま `localPosition` に設定 |
| §8 Humanoid チェック表示 | Inspector 内の警告ボックス＋一覧（選択ボタン・再チェック付き）と、ビルド時の Unity Console `LogWarning`。加えて NDMF Console へ `ErrorReport`（NonFatal）連携 |
| §9 NDMF 警告レポート | Resolving フェーズで `ErrorReport.ReportError(Localizer, ErrorSeverity.NonFatal, key, 対象ボーン, ...)` |
| §10 フォールバック | Editor asmdef の `versionDefines` で `com.vrchat.avatars >= 3.10.6` のとき `KEMOLEG_SDK_3_10_6_OR_NEWER` を定義。未満では Sources リスト直接操作にフォールバック（ベストエフォート） |

## ファイル構成

- `Runtime/KemoLegAimSetup.cs` — 設定用コンポーネント（ボーン参照、ターゲット名、ターゲット6個のローカル Position と初期化フラグ）
- `Runtime/KemoLegTargetName.cs` — ターゲット名生成（CamelCase → UPPER_SNAKE）
- `Editor/BoneAutoDetector.cs` — ボーン自動検出（検出時にターゲット初期位置も生成）
- `Editor/HumanoidCheck.cs` — Humanoid 割り当てチェック（Inspector / プラグイン共用）
- `Editor/TargetPlacement.cs` — 親ボーン対応付け、初期値のワールド→親ローカル換算、ワールド↔親ローカル相互変換
- `Editor/KemoLegAimSetupEditor.cs` — CustomEditor（Inspector＋Scene ギズモ）
- `Editor/KemoLegAimBuilder.cs` — ビルド時の適用本体（ターゲット生成・Constraint 付与）
- `Editor/KemoLegAimPlugin.cs` — NDMF プラグイン登録と警告レポート

## 基準数値例（仕様 v1.1 付記C）

手動セットアップで得られた値（同一モデル、L側のみ）です。
**モデル固有の値であり、既定値として固定はしていません**
（初期値はアバター毎に換算して生成します）。値の桁・範囲の参考としてください。
フィールドにこの値を入れると、手動セットアップと同じ配置になるはずです。

| フィールド | 値 (X, Y, Z) |
|---|---|
| AIMTARGET_DUMMY_UPPER_LEG_L | (-0.00750019122, 0.674555898, -1.03390515) |
| AIMTARGET_DUMMY_LOWER_LEG_L | (0, -0.240718693, 0.299352139) |
| AIMTARGET_DUMMY_FOOT_L | (0, 0, 0) |

参考: 同モデルの Hips ローカル Position は
(-7.17401747e-08, -0.00465902314, 0.862715304)、
VRC Avatar Descriptor の View Position は (0, 1.36500001, 0.0728771165)。

## 変更履歴

### v0.4.1

- **キャプチャの診断強化**: v0.4.0 のキャプチャが（Edit Mode 実行を有効化
  しても）タイムアウトしたため、切り分け可能にした。
  - 5 秒ごとに全 Constraint の現在 Offset を Console へ経過ログ出力
    （再計算が起きているか、値が揺れているか、(0,0,0) のままかを判別可能）。
  - 一時クローンを Hierarchy で選択し、Scene ビューの再描画も促す。
  - 値が非ゼロのまま安定しない場合、または 10 秒経っても (0,0,0) の場合は、
    エディタ文脈での `ActivateConstraint()` キャプチャを試す。
  - タイムアウトを 30 秒に延長し、失敗時に最後の Offset 値を表示する。

### v0.4.0

- **At Rest / Offset の事前キャプチャ機能を追加**（問題の根本対策）:
  実測により、ビルド時の `ActivateConstraint()` は Offset をキャプチャせず
  （現在の値＝既定値 (0, 0, 0) を維持してロックするだけ）、v0.3.0 の
  自前計算値も SDK ソルバの値とは数十度単位でずれることが判明した。
  手動セットアップで値が決まる仕組み（未ロックの Constraint がエディタ上で
  再計算を繰り返す）を再現するため、Inspector に
  「At Rest / Offset をキャプチャ」ボタンを追加した。
  - アバターの一時クローン上でターゲットと未ロックの Constraint を生成し、
    SDK のエディタ上 Constraint 実行に再計算させ、確定値をコンポーネントへ
    保存する。クローンは読み取り後に自動で破棄される。
  - ビルド時はキャプチャ値がそのまま使われる。ターゲット位置を変更すると
    陳腐化警告が出るので再キャプチャすること。
  - 前提: SDK Control Panel の「Execute VRChat Constraints in Edit Mode」
    が有効なこと（無効な場合はタイムアウトとして検出される）。
  - 未キャプチャでもビルドは可能で、その場合は v0.3.0 の計算値フォール
    バックが使われる（姿勢は近似になる）。
- **要確認**: キャプチャ後、Console に出力される L 側の値が手動セットアップ
  側（v0.3.0 の README に対照表あり）と一致すること。その状態でビルドし、
  Play の姿勢が手動セットアップと一致すること。

### v0.3.0

- **Play で姿勢が崩れる問題への対策**: v0.2.0 ではクローン側の
  Rotation At Rest / Rotation Offset が (0, 0, 0) のまま固定されていた
  （手動セットアップ側は部位ごとの値を持つ）。対策として以下を変更。
  - `ApplyConfigurationChanges()` → `ActivateConstraint()` の順に変更し、
    Source が適用された状態で Offset をキャプチャさせる。
  - Rotation At Rest にボーンのローカル回転（レストポーズ）を明示設定。
  - それでも Rotation Offset が (0, 0, 0) の場合は、レストポーズの変換から
    計算した値で補い、警告ログを出す。
- **Aim / Up Vector を手動セットアップの実測値に合わせた**:
  Aim は全部位 (0, 0, 1)。Up は LowerLeg のみ (0, 0, 1)、他は (0, 1, 0)
  （v0.2.0 までは全て既定値 (0, 1, 0) だった）。
- **要確認**: ビルド後、クローン側 L の Rotation At Rest / Rotation Offset が
  手動セットアップ側の値と一致するか（オイラー角の 360° 表記差は可）。
  UpperLeg: At Rest (34.9947395, 179.492432, 179.128738) /
  Offset (35.2114067, 102.273621, 97.0595703)。
  LowerLeg: At Rest (86.208786, 170.793869, 171.190704) /
  Offset (-76.0221558, 113.827377, 66.8026276)。
  Foot: At Rest (-86.3867188, -179.998291, 179.998245) /
  Offset (81.2906036, 122.807159, 122.505249)。

### v0.2.0

- **ターゲットの階層構造を訂正**（仕様 v1.1 付記A）: v0.1.0 ではターゲットを
  対応するトラッキングボーン配下に生成していた（誤り）。正しくは
  Upper→LowerLeg、Lower→Foot、Foot→Foot の子に生成する。
- **位置の保持・編集方式を変更**（仕様 v1.1 付記B）: 割合スライダー
  （前方1軸）を廃止し、ターゲット6個それぞれのローカル Position (X, Y, Z) を
  コンポーネントに保持して数値編集する方式に変更。初期値は旧割合方式から
  換算して自動生成され、「初期値を再計算」ボタンでのみ再生成される。
  ギズモはワールド位置表示＋親ローカルへの逆算に更新。
- **移行上の注意**: 割合フィールド（UpperLegOffsetRatio 等）を削除したため、
  v0.1.0 で設定した割合値は v0.2.0 には引き継がれません。既存コンポーネントは
  未初期化として扱われ、次回の自動検出実行時（または「初期値を再計算」）に
  初期値が再生成されます。

### v0.1.0

- 初版試作（仕様 v1.0）。

## 実地確認事項（試作フェーズで要検証）

このVMには Unity が無いため、本パッケージはコンパイル・動作未検証です。
実プロジェクトで最初に以下を確認してください。

1. **型定義の直接確認**（両環境: 2022.3.22f1 + SDK 3.10.6 / 6000.0.67f1 + SDK 3.11.0-beta.1）
   - `VRCAimConstraint` / `VRCConstraintBase` の継承チェーンと、asmdef の参照設定
     （`VRC.SDK3.Dynamics.Constraint.dll` 等）が解決するか。
   - `WorldUp` / `WorldUpTransform` プロパティ名が調査結果どおりか。
2. **Activate と Apply の順序**: `ActivateConstraint()` と `ApplyConfigurationChanges()`
   の正しい呼び出し順序は調査で未確定。本試作では公式 API ページの対応表の並びに倣い
   Activate → Apply の順にしている（`KemoLegAimBuilder.cs` に TODO あり）。
   逆順との挙動差を実プロジェクトで検証すること。
3. **AimAxis / UpAxis の既定値**: 元手順では変更しないため既定値のままにしている。
   既定値が元手順の Inspector 初期状態と一致するか確認すること。
4. **フォールバック経路（SDK 3.10.6 未満）**: `VRCConstraintSourceKeyableList` の
   正確な操作 API（`Sources.Add` の可否）は旧 SDK 環境で未確認（TODO あり）。
   正式サポート範囲外のためベストエフォート。
5. **実機（フルトラ）動作**: ビルド時に生成した Constraint が、実機の
   フルボディトラッキング環境で元手順どおりに動くか（踵の動き・爪先の一致）。
6. **Unity 6 + ベータ SDK での確認**: SDK 正式版リリース時に Constraints API の
   差異がないか再確認すること。
7. **Humanoid チェックのフェーズ**: Resolving で実施しているが、他のプラグインとの
   実行順序の影響がないか確認すること。
8. **近傍性スコアの閾値**: `BoneAutoDetector` の近傍性判定は簡易な固定閾値。
   スケールの異なるアバターでの検出精度を確認すること。
9. **v0.2.0 の階層と Position（最優先）**:
   - ビルド後のクローンで、生成される階層が手動セットアップ例と一致すること
     （Foot_x の子に AIMTARGET_…_FOOT_x と AIMTARGET_…_LOWER_LEG_x、
     LowerLeg_x の子に AIMTARGET_…_UPPER_LEG_x）。
   - 「基準数値例」の値を L 側フィールドに入力してビルドし、手動セットアップと
     同じ配置・動きになること。
   - 既存アバターの v0.1.0 コンポーネントが v0.2.0 で開けること（割合値は
     引き継がれず、初期値が再生成される想定）。
