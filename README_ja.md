# kPlug Patcher: Invite Character ID

本プラグインは、Koikatu において `kPlug` を使用している環境で発生する招待まわりの不具合を解決するためのパッチプラグインです。
**kPlug に対するパッチ処理はこのプラグインに集約する**方針とし、他プラグイン（`KK_AlternativeVoice` 等）は kPlug の挙動に手を出しません。

[English](README.md) / 日本語

**kPlug の作者の方へ:** 本プラグインが回避している不具合と、パッチしていない少数の例外を、スタック・原因・kPlug の中での直し方の案とともに [docs/KPLUG_ISSUES.md](docs/KPLUG_ISSUES.md)（英語）にまとめています。本プラグインと文書は MIT ライセンスなので、どの部分も kPlug に取り込んでかまいません。

## 1. 背景と解決する問題

### 1.1. 同名キャラを複数招待できない
`kPlug` の招待機能はキャラクター名をキーとして管理・ロードを行っているため、同一名のキャラクターを招待リストに追加すると、適切にロードされなかったり、内部的な競合が発生したりする問題がありました。

### 1.2. 招待が「クリックしても無反応」になる
`kPlug` の `SpotManager.AdaptCaliber()` が**破棄済みのボーン参照**に触れて `NullReferenceException` を起こすと、 `SpotManager.Start()` が中断して `spotsAreInit` が永久に `false` のままとなり、`Dealer.InProcess` が `true` でロックされます。
以降の招待クリックは `if (!InProcess)` ガードによって**ログにも出ないまま無視される**ため、ユーザーからは完全な無反応に見えます。
特定のキャラカードをメインヒロインにしたときに発生しやすいことを実機ログで確認しています。

## 2. 主な機能
- **招待カードの複製＋改名**: カード招待時（`Dealer.InviteCard`）に、招待されるキャラカードデータを**メモリ上で複製**し、その複製の名前に連番を付与します（例: "春野千佳" -> "春野千佳 (1)", "春野千佳 (2)"）。
- **整合性の維持**: 改名がキャラクターデータそのものに適用されるため、`kPlug` の在席判定・生成されるキャラクター・表示名辞書・名前指定APIのすべてが同じ名前を参照します。これにより退場処理や名前指定操作が正しく動作します。なお在席リストの持ち方は非VR版（`InviteUI.PresentNames`）とVR版（`mainAi`/`secondAi`/`harem` から都度組み立て）で異なるため、両方式に対応しています。
- **ゲーム内での識別**: 同じキャラカードを複数招待しても、それぞれが別の名前を持つため、ゲーム内および他プラグインから別個体として区別できます。
- **招待無反応の回避 (v1.2.0〜)**: `SpotManager.AdaptCaliber` の直前に破棄済みのボーン参照を検出し、 kPlug 自身の `BonesCtrl.FindBones()` を呼び直して復旧します。独自のボーン探索は行わないため影響範囲が限定されます。
- **衣装変更後の破棄済み参照の回避**: 招待・キャラ交代後に衣装が再構築されると、kPlug の `ColDefsManager` / `BindersCtrl` / `BonesCtrl` が破棄済みの参照を掴んだまま毎フレーム例外を出し続けることがあります。破棄済み要素の除去と `BonesCtrl.FindBones()` の再呼び出しで復旧します。
- **絶頂ループの回避（ゲーム本体へのパッチ）**: 上記と同種の原因で、ゲーム本体の `ParentObjectCtrl` が破棄済みの小物 GameObject を参照して例外を起こし、その結果 `HSonyu.Proc` の絶頂終了処理（ゲージのリセット）が実行されずに絶頂状態がループする不具合を回避します。
- **kPlugVR の null チェック漏れへのガード (v1.3.1〜)**: kPlugVR 本体が出す NullReferenceException のうち、原因が特定できたものを原型の挙動を変えない範囲で防ぎます（`KPlugVRNullGuards`）。
  `VRCtrl.SetMenuVisibility` / `VRCtrl.LateUpdate`（タイトル用 VRCtrl の自壊フレーム）、 `HinputParser.Chain`（入力の未代入）、`AI_Public.LateUpdate`（生成直後の `bc.kb`）。
  ガードは kPlug の型が見つかり次第（frame=1〜）適用します。
- **kPlug 3.599 以降（新API）への対応 (v1.4.0〜)**: バージョン番号ではなく「対象メソッド・型が在るか」で分岐します。 VR版の招待入口 `InviteInVR` への改名パッチと、新しい招待UI（`MenuInvite`）で同じカードを再び選べるようにする回避策を持ちます。
- **招待メニューのカード一覧の遅延読み込み (v1.5.0〜)**: kPlug は Hシーン入場のたびに全キャラカードを完全な形で読み込み保持します（1枚約30MB、約100枚で約3GB）。一覧には名前・性格と顔画像だけの軽量版を置き、
  招待する瞬間に読み直すことで、通常モードでは Mono の最大値が 5.4GB → 1.75GB になりました（`LazyInviteCardList`）。
  v1.6.0 からは男性一覧（バディ招待用）も対象で、`Dealer.InviteBuddy` の時点で読み直します。
  v1.7.0 からは、一覧用のカードを最初から全身画像・顔画像・パラメータだけ読みます（部分読み込み）。
  Hシーン入場時の招待メニュー作成は、約12秒・コミット +2GB から、約1秒・+410MB になりました（通常モードで確認）。
- **終了確認をキャンセルした後の不具合の修正 (v1.5.2)**: ウィンドウを閉じる操作で出る終了確認を「いいえ」で閉じると、以後、改名・読み直し・各種回避策が止まっていた（招待キャラが白くなる）問題を修正しました。
- **複数人招待時のフレームレート改善 (v1.8.0〜、`DynamicBoneThrottle`)**: 複数人招待時の重さの主因は、ゲーム本体の揺れもの（DynamicBone）でした（5人でフレーム時間の約6割）。招待キャラとバディの揺れもの（髪・スカート・アクセサリ）の計算を止め、全キャラの揺れものの計算を1フレーム1回までに抑えます。通常モードで、5人時のフレーム時間が 123〜172ms（6〜8fps）から 28〜37ms（27〜36fps）になりました。招待キャラの揺れものは初期の姿勢で静止します（胸など体の揺れは残る）。オプションで個別に有効/無効切り替え可能。
- **Ahegao表示中の毎フレーム NRE の抑止 (v1.8.1、`EnableAIMainFaceColorGuard`)**: kPlug 設定の `EnableAhegao` を有効にしていると、`AI_Main.Ahegao()` が `Core.heroineCha.customMatFace`（顔のマテリアル）を null チェックせず `SetColor` するため、条件が揃っている間ずっと毎フレーム NRE が出ます（VR実機ログで 9,130 回）。例外で `Ahegao()` 末尾の状態記録と、呼び出し元 `LateUpdate()` の残りも飛んでいました。マテリアルが使えないときだけ頬の色の上書きを飛ばし、残りは実行するようにします。VRの実機で、抑止 4,666 回・NRE 0件・プレイ上の問題なしを確認済み（2026-09-23）。
- **招待キャラへのキスが終わる瞬間の NRE の抑止 (v1.8.5、`EnableHCtrlKissGuard`)**: kPlugVR 3.56 の`HCtrl.ParseTouchables()` は、招待キャラへのキスの範囲から外れてキスの相手（`kissable_ai`）が null になったフレームでも、キス中の目印（`inAiKiss`）がまだ立っているため `kissable_ai.KissByPlayer()` を呼び、NRE になります（招待キャラへのキスが終わるたびに1回前後）。呼び出しの前に null チェックを差し込み、null のときはその呼び出しだけを飛ばします。原型でもそこで例外になり、メソッドの最後の文なので、失われる処理はありません。kPlug 3.63 の VR は作者が同じ null チェックを入れているため、IL を読んで適用しません。VRのみ対象。VR の実機で NRE 0件を確認済み（2026-09-26）。
- **招待キャラ複数との同時キス (v1.9.0、`EnableMultiGuestKiss`)**: kPlugVR の `HCtrl.ParseTouchables()` は、キスの範囲に入っている招待キャラのうち最初の1人としかキスしません。ほかの招待キャラがキス中のときに限り、`StartDistance` / `StartAngle` の範囲に入った2人目以降のキャラにも kPlug のキス（`KissByPlayer()`）を始め、`KeepDistance` / `KeepAngle` の範囲を出たら終えます（1人だけのキスは kPlug 本来の範囲のまま）。2人目以降がいる間は kPlug が選んだキャラも同じ範囲で続けます（kPlug のキス終了を止める）。同時にキスした全員にキス回数・経験値・興奮が入り、キスの声が重なります。KK_CyuVR を入れていれば全員の口と舌が動きます。あわせて、キスの相手が同じフレームで別の招待キャラに直接替わると前の相手がキス状態のまま残る kPlug の問題の後始末（`EnableStuckGuestKissFix`）も入れました。VRのみ対象。VR の実機で、2人との同時キスが3回とも始まり、3回とも2人とも範囲を出たところで終わって、キス状態のまま残るキャラが無いことを確認済み（2026-09-26）。
- **在席中のキャラと同名のカードを招待リストに出す (v1.8.2、`ShowSameNameCardsInInviteList`)**:
  kPlug は `InviteUI.IsAlreadyInHarem` で**名前が一致するカードを一覧から外す**ため、メインヒロインに選んだキャラと同名のカードが招待できませんでした。本プラグインは招待の瞬間に複製を作って改名するので、同名でも別個体として扱えます。カード一覧（女性・男性）の除外だけを解除します。ストーリー／デート相手のヒロインはセーブデータ由来で改名しないため、従来どおり除外されます。通常モードの実機で、メインヒロインと同名のカードを2枚とも招待できることを確認済み（2026-09-23）。
- **HVoiceCaching 併用時のキャラ交代の不具合の修正 (v1.8.3、`EnableHVoiceCachingShortBreathFix`)**: HVoiceCaching 0.3 は短い吐息の辞書を空にせずに読み込むため、kPlug のキャラ交代で `HVoiceCtrl.Init` が呼び直されるたびに例外（`An element with the same key already exists`）が出ていました（実機ログで交代 27回中 27回）。例外で交代処理（`AI_Public.IE_SwapGirls`）の後半が飛ぶため、kPlug がキャラごとの声の性格を覚え直せず、交代を重ねるうちに**別の性格の声で話す**ようになります。短い吐息もHシーン開始時の性格のまま変わりませんでした。交代で外に出たキャラが「交代中」（`InSwap`）のまま残る問題もありました。HVoiceCaching の直前に、本来のゲーム処理と同じく辞書を空にします。HVoiceCaching が入っていない環境では何もしません。通常モードの実機で、交代4回（同じキャラがメインに戻る交代2回を含む）で例外0件・声の性格の食い違い0件を確認済み（2026-09-24）。VR でも交代32回で例外0件（2026-09-25）。
- **HVoiceCaching 併用時に 3P の Hシーンのロードが止まる不具合の修正 (v1.9.1、`EnableHVoiceCachingVoiceListFix`)**: HVoiceCaching 0.3 は、2人目の声のキャッシュ（`<性格>_Voice_1.cache`）が無いと、1人目をキャッシュから読み込んだまま本来の読み込みに処理を戻します。本来の読み込み（`HVoiceCtrl.LoadVoice`）は空の辞書を前提にしているため例外（`An element with the same key already exists`）になり、Hシーンの準備が止まってロード画面のまま進みません。例外で2人目のキャッシュも保存されないので、同じ組み合わせでは毎回止まります（`*_Voice_0.cache` はほとんどの性格で既にあるため、実質 3P はほぼ必ず止まる）。本来の読み込みが行われるときだけ、その直前に声の辞書を `HVoiceCtrl.Init` 直後と同じ状態に戻します。HVoiceCaching が2人分をキャッシュから読んだとき、HVoiceCaching が入っていない環境では何もしません。VR の実機で、2人目のキャッシュが無い 3P（c25＋c30）のロードが止まらず、2人目のキャッシュが保存されることを確認済み（2026-09-26）。
- **キャラ交代後の表情を交代後のキャラの性格に合わせる (v1.8.4、`EnableFaceListReloadOnSwap`)**: Hシーンの表情は、セリフごとに決まった「表情番号」を性格ごとの表情リスト（`HFaceList`）で眉・目・口の形に変えて作ります。kPlug のキャラ交代は声のリストだけを読み直し、表情リストを読み直さないため（kPlug 3.56 / 3.63、通常版 / VR版とも）、交代後のキャラは**Hシーン開始時のメインキャラの性格の表情リスト**で顔を作っていました。表情番号の意味は性格ごとに違うので、セリフと合わない顔になります。`HVoiceCtrl.Init` が別の性格で呼び直されたときに、表情リストも同じ性格で読み直します。Hシーン開始時は表情が先に同じ性格で読まれているので、読み込みは増えません。その性格の表情リストが見つからない場合は、前の性格のまま使います。kPlug は招待キャラの表情にもメインキャラの表情リストを使うため、招待キャラの表情も「今のメインキャラの性格の表」になります（これまでは Hシーン開始時のメインキャラの性格）。通常モードの実機で、交代4回すべてで交代先の性格の表情リストに読み直されること、Hシーン開始時には読み直さないことをログで確認済み（2026-09-25。見た目の違いと VR は未確認）。
- **アヘ顔中の招待キャラの表情の NRE の抑止 (v1.10.3、`EnableGuestFaceColorGuard`)**: v1.8.1（`EnableAIMainFaceColorGuard`）の招待キャラ版です。kPlug の招待キャラの表情のコルーチン `FaceCtrl.IE_SetFace` は、アヘ顔のときに顔のマテリアル（`customMatFace`）を null チェックせず `SetColor` するため、マテリアルが null のキャラでは表情が変わるたびに NRE が出ます（通常モードの実機ログで、特定の招待キャラ1人に 86件）。例外で頬の色の上書きと、最後の「今の表情の記録」が飛んでいました。マテリアルが使えないときだけ頬の色の上書きを飛ばし、残りは実行します。どのキャラで起きたかをログに出します。VR の実機で、この NRE が 0件（前回 86件）、3人のキャラで計 250回スキップしたことを確認済み（2026-09-28）。
- **3P の2人目のアヘ顔の毎フレーム NRE の抑止 (v1.10.4、`EnableAISecondFaceColorGuard`)**: v1.8.1（`EnableAIMainFaceColorGuard`）の2人目版です。kPlug の `AI_Second.Ahegao()` は、アヘ顔の表示中に2人目の顔のマテリアル（`Core.heroine2Cha.customMatFace`）を null チェックせず `SetColor` するため、顔の素材を指定していない顔 MOD のキャラが2人目だと毎フレーム NRE が出ます（VR の実機ログで 18,047 件。ログの肥大化の主な原因）。例外で `Ahegao()` 末尾の状態の記録と、`LateUpdate()` の残り（2人目のまばたきの設定、ぶっかけのマスク作成）が飛んでいました。マテリアルが使えないときだけ頬の色の上書きを飛ばし、残りは実行します。VR・通常モードの両方が対象。KK の通常モードの実機で、この NRE が 0件になり、1人目・2人目それぞれでスキップしたことを確認済み（2026-10-11）。VR は未確認です。
- **液体の模様のコピーの NRE の抑止 (v1.10.5、`EnableCopyLiquidMapGuard`)**: kPlug の `ChaTools.CopyLiquidMapToMat()` は、体・顔の液体（汁）の模様をメインヒロインのマテリアルから写すときに、写し元・写し先のマテリアル（`customMatFace` / `customMatBody`）を null チェックせずに使います。顔の素材を指定していない顔 MOD のキャラがいると、Hシーン開始時に NRE が1〜4件出て、呼び出し元（`DickBehavior.Start()`）の残りも飛んでいました。どちらかが null のときだけコピーを飛ばし、「コピーしなかった」（false）を返します。VR・通常モードの両方が対象。KK の通常モードの実機で、この NRE が 0件になり、液体の表示が変わらないことを確認済み（2026-10-11）。
- **IK の目標が null になって起きる絶頂ループの回避 (v1.10.6、`EnableExtraAnimIkTargetGuard` / `EnableMotionIKNullTargetGuard`)**: kPlug の追加アニメーション（`ToolExtrAnimIK.SetIkTargets`）は、アニメーションの切り替えのたびに手足の IK の目標をキャラの中から名前で探して付け直しますが、見つからないと null をそのまま入れます。キャラの骨格が一時的にキャラの外にあると（例: KK_SetParentVR で男の位置を動かしている間）、ゲーム本体の `MotionIK.LinkIK` がアニメーションの切り替え（`HActionBase.SetPlay`）の途中で例外になり、絶頂の終了処理が飛んで絶頂が繰り返されていました（絶頂ループ）。kPlug 側では null にされた目標を元に戻し（`IkHook.Create` もフック先が null なら作らない）、ゲーム本体側の安全網として、目標が null の手足だけを飛ばしてアニメーションの切り替えを最後まで実行させます。後者は**ゲーム本体(Assembly-CSharp)へのパッチ**です。VR・通常モードの両方が対象。kPlugVR の実機で適用を確認済み（2026-10-19）。絶頂ループが起きた条件はまだ再現できておらず、効果は未確認です。
- **キャラ交代後のお腹の膨らみの毎フレーム NRE の回避 (v1.10.7、`EnableBellyCtrlStaleBoneFix`)**: kPlug の `BellyCtrl`（妊娠設定が有効なときのお腹の膨らみ）は、骨（`cf_s_waist01/02`・`cf_s_spine01/02`）を初期化時に一度だけ取得し、取り直す処理を持ちません。キャラ交代（`AI_Public.IE_SwapGirls`）で2人の骨の階層が作り直されると、破棄済みの骨に書き込み続けて毎フレーム NRE になります（kPlugVR で1セッション 2,376 件）。破棄済みの骨だけを kPlug 自身の `ChaTools.FindInCha()` で探し直して差し替えます。復旧できるまでの間は、その骨の書き込みだけを飛ばします。`EnableCoordinateChangeStaleRefFix` も有効なときに働きます。kPlug・kPlugVR の両方が対象。kPlugVR の実機で NRE 0 件・復旧のログを確認済み（2026-10-19。お腹の見た目と通常モードでの交代は未確認）。
- **続けて着替えたときの骨の復旧の待ちをなくす (v1.11.2)**: `BonesCtrl`・`BellyCtrl` の破棄済みの骨の復旧は、直らないときに FindBones を毎フレーム呼ばないよう次の試行まで 60 フレーム待ちますが、復旧に成功した後もこの待ちが残っていました。そのため、同じキャラの着替えボタンを続けて押すと、2回目以降は待つ間 `BonesCtrl.LateUpdate` の NRE が毎フレーム出ていました。復旧に成功したとき・骨が健全に戻ったときに待ちを戻すようにしました（待つのは直らなかったときだけ）。KK の通常モードの実機で、続けて着替えて 53 回復旧し、この NRE が 0 件（修正前は 295 件）になったことを確認済み（2026-10-20）。VR・KKP は未確認です。
- **GUID の変更と、通常モードのログの整理 (v1.11.0)**: GUID を `kkttspj.kk.kplugpatcher` / `kkttspj.kkp.kplugpatcher` に変え、1.10.x までの設定ファイルを初回の起動で引き継ぎます（§7）。通常モードで kPlugVR の型へのパッチが当たらないとき（kPlugVR のクラスをロードできないため。実害はありません）のログを、Error から Info に下げました。
- **招待キャラの表情のコルーチンの NRE の診断 (v1.10.2、`EnableSetFaceNreDiagnostics`)**: 読み取り専用の診断（既定は無効）。kPlug の `FaceCtrl.IE_SetFace` が NullReferenceException を出したときだけ、キャラ名・表情の種類・アヘ顔の状態と、コルーチンがたどる参照（`ai.Cha`、`Core.config`、`hProc.voice.faceLists[0]`、`mouthCtrl`、`customMatFace` など）が null かどうかをログ出力します。例外は抑止しません。
- **性能・メモリの診断 (v1.6.1〜、`KPlugMemoryDiagnostics` / `KPlugPerfDiagnostics`)**: 読み取り専用の診断。
  30秒ごとのフレーム時間と kPlug・ゲーム本体のメソッド別の処理時間、キャラの読み込み・交代の所要時間、招待の前後のメモリ、シーン変化時の RenderTexture の作成元別の内訳などをログに出します。

### 併用を推奨するプラグイン（kPlug 環境の負荷対策。本プラグインの外）
- **KK_METextureLeakFix**（自作の独立プラグイン）: KK_MaterialEditor 4.0.2 が、キャラを破棄してもテクスチャ（RenderTexture）を解放しない問題の回避策。kPlug で違うカードを招待するたびに積み上がっていた約 4GB が、Hシーンを出た後に解放されます（退出後のコミット 9.2GB → 5.4GB）。
- **HVoiceCaching 0.3**（Picolet21、既存のプラグイン）: Hシーン開始時と、kPlug の交代で性格が変わるときの声リストの読み込み（`HVoiceCtrl.Init`、毎回約11秒）をディスクにキャッシュします。2回目以降の交代は約13秒 → 約2〜3.5秒になりました。性格の声リストを変える MOD や DLC を入れ替えたときは `BepInEx\cache\KK_HVoiceCaching` を削除してください。**0.3 には交代のたびに例外を投げる不具合があり、本プラグイン v1.8.3 以降の `EnableHVoiceCachingShortBreathFix` で回避します**（無効にして併用すると、交代を重ねるうちにキャラの声の性格が入れ替わります）。**また 3P（女の子2人）の Hシーンがロード画面のまま止まる不具合もあり、v1.9.1 以降の `EnableHVoiceCachingVoiceListFix` で回避します。**

### 安全性に関する設計方針
- ディスク上のキャラカードファイルは**一切変更しません**。
- 招待UIが保持しているキャラカード実体も**変更しません**（複製に対してのみ改名するため、招待UIの表示名は素のままです）。
- セーブデータ由来のヒロイン招待には**介入しません**（セーブデータの氏名を汚さないため）。

## 3. 導入方法
1. **Koikatu (KK)** の場合は `kPlugPatcher_InviteChrID.dll` を、
   **Koikatsu Party (KKP)** の場合は `kPlugPatcher_InviteChrID_KKP.dll` を、
   `[ゲームのインストール先]\BepInEx\plugins\kPlugPatcher_InviteChrID\` （または適切なプラグイン配置フォルダ）にコピーします。
   **ゲームに対応する方のDLLのみを配置してください**（両方を同時に入れる必要はありません。誤って両方入れても `[BepInProcess]` により該当しない方は自動的にスキップされます）。
2. ゲームを起動します。

### 3.1. ソースからビルドする場合
本プロジェクトはゲーム本体と BepInEx の DLL を**相対パスで参照**します
（`..\..\BepInEx\core\`、`..\..\Koikatu_Data\Managed\`）。そのため、

1. `kPlugPatcher_InviteChrID` フォルダごと **ゲームのインストール先の直下**に置きます。
2. そのフォルダで `build_and_deploy_invite.ps1` を実行します（.NET SDK が必要です）。

KK版（`Release_KK`）と KKP版（`Release_KKP`）が両方ビルドされ、`..\Deploy\kPlugPatcher_InviteChrID\` に出力されます。別の場所に置いてビルドしたい場合は、`kPlugPatcher_InviteChrID.csproj` の `HintPath` と `GameManagedPath` を環境に合わせて書き換えてください。

## 4. 依存関係
- **kPlug**: 動作には `kPlug` が必須です。
- 本プラグインは `kPlug` の SoftDependency として実装されており、`kPlug` が未インストールの場合や、必要なクラスが検出できない場合は、自動的に機能を無効化して安全に動作します。

## 5. 技術詳細
- Harmony パッチを利用し、`InviteCard(ChaFileControl)` の Prefix で招待カードを複製・改名しています。
- 非VR版 `kPlug.dll` の `kPlug.CmpH.Dealer` と、VR版 `kPlugVR.dll` の `kPlugVR.CmpBase.Dealer` は**完全に別のクラス**であり、両方に個別にパッチを適用する必要があります（v1.1.1 で対応）。
- ロード順序の影響を避けるため、起動後一定時間の経過を待ってから動的にパッチを適用する方式を採用しています。
- **kPlug の型は全てリフレクション＋型ごとの辞書で扱い、静的参照しません**（v1.3.0 で徹底）。同名クラスが別名前空間に存在するため、片方を静的参照すると**もう一方のモードで黙って動かなくなります**。この事故を構造的に防ぐため、csproj から `kPlug.dll` への参照そのものを削除しています。**参照を復活させないでください。**
- **Koikatu の実行環境 (Unity 5.6 / Mono / net35) では `RegexOptions.Compiled` を使用しないこと**（静的コンストラクタで例外が起き、その型を使う全処理が恒久的に失敗する）。
- **KK/KKPでゲームごとに個別ビルドしています**（v1.3.0〜、`Release_KK`/`Release_KKP`の2構成）。

## 6. 既知の制約
- **解決済み（2026-08-06）**: 招待・キャラ交代の後に衣装が再構築されると、kPlug の `BindersCtrl` /  `ColDefsManager` / `BonesCtrl` が破棄済みの参照を掴んだまま毎フレーム `NullReferenceException` を出し続け、さらにゲーム本体側の `ParentObjectCtrl` の同種の実装漏れが絶頂終了処理を中断させて**絶頂ループ**を引き起こしていました（特定のキャラクターカードで再現）。 `SpotManagerBonesFix` は `AdaptCaliber` の直前でしか修復しないため本事象には間に合わず、別途 `CoordinateChangeStaleRefFix` / `ParentObjectCtrlStaleRefFix` を実装し、実機で解消を確認済みです。**VRモードでも全ての回避策が正常に適用・動作することを実機で確認済みです**（2026-08-07）。
- **VRモードでは `InviteUIDiagnostics`（診断機能）のみ自動的に無効化されます。** VR版の `InviteUI` は非VR版と構造が異なり（`isValidCtx` / `isOpenable` を持たない）、診断に必要なフィールドを解決できないためです。**診断機能のため実害はなく**、招待・改名を含む本体機能はVRモードでも正常に動作します。
- **非VRモード（KK/KKPとも）での改名処理・招待無反応の回避策は実機確認済みです** 同一カードの複数招待で `(1)` `(2)` の連番付与、`SpotManager.AdaptCaliber` の例外0件を確認しています。
- **VRモードでの改名処理は v1.3.0 で修正し、kPlugVR 3.5.6 の実機ログで動作を確認済みです** （2026-09 の各VRログで `… -> … (1)` の改名を確認）。v1.2.0 以前は処理本体が非VR版の `kPlug.Core` を静的参照していたため、VRモードでは `Core.inviteUi` が null となり改名が一度も実行されていませんでした。VR版は在席リストの持ち方自体が異なる（`PresentNames` が存在しない）ことが判明したため、在席リストの取得方法ごと作り直しています。
- **kPlug のバージョン対応（2026-10-20 時点）**:
  - **kPlug 3.5.6（旧API）**: 通常モード・VRモードとも実機で確認済み。
  - **kPlug 3.599 以降（新API）**: 機能検出で対応（v1.4.0）。**kPlug 3.6.3**（2026-10-19 の更新を含む）では、通常モード・VRモードとも回避策が適用されることを実機のログで確認済み。VR で `BellyCtrl`（v1.10.7）と `AI_Public.LateUpdate` の `bc.kb` のガードが働いたことも確認しています。それ以外の回避策の効果は、主に 3.5.6 で確認したものです。
    破壊的変更は 3.59 → 3.599 の1段で入っている。VR版では `VRCtrl` / `HinputParser` が廃止されたため、それらを対象とするガードは対象外になる（ログに理由を出す）。
- **招待カード一覧の遅延読み込み（v1.5.0）の制約**:
  - 通常モード（kPlug 3.5.6）で実機確認済み（女性の見た目も目視で確認。招待キャラとの交代も繰り返し行い、異常なし）。**VRモードは未確認。**
  - 男性一覧の軽量化（v1.6.0）は通常モードで確認済み（Mono の最大が、本プラグインなしの 4.8GB から 1.16GB に）。設定 `IncludeMaleCards = false` で v1.5.2 以前の動作（完全版のまま）に戻せる。
  - 部分読み込み（v1.7.0）は、`【KoiKatuCharaSP】` など識別子の違うカードでは使えず、従来どおり完全に読み込む（ログに理由を1回出す）。
- **v1.6.0〜v1.8.0 の機能（男性一覧の軽量化、部分読み込み、各種診断、DynamicBoneThrottle）は、VRモード（kPlugVR 3.5.6）でも動作を確認済み**（2026年9月22日、約1時間27分のセッション）。招待メニューの作成 414ms・カード99枚すべて部分読み込み成功、揺れものは1フレーム1回に制限（4人招待で 20〜22fps を維持）、退出時に Material Editor のテクスチャ 356件を解放。ただし VR では 1眼あたり 2112×2304 を2枚描くため、フレーム時間の残り 25〜30ms は描画で、ここは本プラグインの対象外。
- **診断機能（`KPlugPerfDiagnostics`）は、通常モードで kPlugVR の型に深く触れると Mono ごと落ちる**（v1.7.1 で発生、v1.7.2 で修正）。全アセンブリを走査する処理を足すときは、動作中のモードのアセンブリだけに絞ること。
  - kPlug 3.599 以降の VR（`MenuInvite`）は未対応。3.63 の非VRは同じ構造のため効く見込み（未確認）。
  - カードファイルをゲーム中に移動・削除すると、招待時の読み直しに失敗し、既定の見た目で招待される。
- メインヒロイン・セカンドヒロイン（セーブデータ由来）と同じ姓名を持つ別カードは、招待候補一覧にそもそも表示されない場合があります（kPlug側の在席判定が本プラグインの改名処理より手前の段階で行われるため）。
- **サードパーティ製チートツール`KK_CheatTools`（[ManlyMarco/IllusionCheatTools](https://github.com/ManlyMarco/IllusionCheatTools)）の「Unlock invalid H positions as well」設定が有効だと、Inviteアイコンが恒久的に無反応になります。** この設定はゲーム本体のHシーン体位絞り込み(`H.HSceneProc.CreateListAnimationFileName`)を無効化し、本来選ばれないはずのDLC専用体位が毎回Hシーン開始体位として選ばれてしまうため、kPlugの`isValidCtx`判定が`Normal`以外の値に固定されてしまいます。kPlug・本プラグインどちらの不具合でもなく、対処は当該チート設定を無効化することです。

## 7. 設定項目

初回起動時に `BepInEx\config\kkttspj.kk.kplugpatcher.cfg`（KKP は `kkttspj.kkp.kplugpatcher.cfg`）が生成されます。

1.11.0 で GUID を `com.your.kk.kplugpatcher` / `com.your.kkp.kplugpatcher` から変えました。1.10.x までの設定ファイル（`com.your.kk.kplugpatcher.cfg` など）は、1.11.0 の初回の起動で新しい名前に写して引き継ぎます（古いファイルは残します。不要なら削除してかまいません）。

| セクション | キー | 型 | 既定値 | 説明 |
|---|---|---|---|---|
| `SpotManagerBonesFix` | `EnableSpotManagerBonesFix` | bool | `true` | 招待が無反応になる不具合の回避策の有効/無効 |
| `SpotManagerBonesFix` | `VerboseSpotManagerDiagnostics` | bool | `false` | `AdaptCaliber` 呼び出しごとの詳細診断ログ。原因調査時のみ有効化 |
| `InviteUIDiagnostics` | `EnableInviteUIDiagnostics` | bool | `false` | Inviteアイコンの状態変化を追う調査用診断（読み取り専用） |
| `HStartCtxDiagnostics` | `EnableHStartCtxDiagnostics` | bool | `false` | `Core.hStartCtx` 異常値の調査用診断（読み取り専用） |
| `HSceneCategoryDiagnostics` | `EnableHSceneCategoryDiagnostics` | bool | `false` | ゲーム本体の体位カテゴリ選択ロジックの調査用診断（読み取り専用） |
| `CoordinateChangeStaleRefFix` | `EnableCoordinateChangeStaleRefFix` | bool | `true` | 衣装変更後に kPlug が破棄済み参照を掴み続ける不具合の回避策の有効/無効 |
| `CoordinateChangeStaleRefFix` | `EnableBellyCtrlStaleBoneFix` | bool | `true` | キャラ交代後に `BellyCtrl`（お腹の膨らみ）の破棄済みの骨を `ChaTools.FindInCha()` で取り直し、取り直すまではその骨の書き込みを飛ばす（v1.10.7）。`EnableCoordinateChangeStaleRefFix` も有効なときだけ働く。VR・通常モードの両方が対象 |
| `ParentObjectCtrlStaleRefFix` | `EnableParentObjectCtrlStaleRefFix` | bool | `true` | 絶頂ループの回避策の有効/無効。**ゲーム本体(Assembly-CSharp)へのパッチ** |
| `MotionIKNullTargetGuard` | `EnableExtraAnimIkTargetGuard` | bool | `true` | kPlug の `ToolExtrAnimIK.SetIkTargets` が手足の IK の目標を null にしたら元に戻し、`IkHook.Create` はフック先が null なら作らない（v1.10.6）。VR・通常モードの両方が対象 |
| `MotionIKNullTargetGuard` | `EnableMotionIKNullTargetGuard` | bool | `true` | ゲーム本体の `MotionIK.LinkIK` で、IK の目標が null の手足だけを飛ばす安全網（v1.10.6）。絶頂ループの対策。**ゲーム本体(Assembly-CSharp)へのパッチ** |
| `LazyInviteCardList` | `EnableLazyInviteCardList` | bool | `true` | 招待メニューのカード一覧を遅延読み込みにする（kPlug 3.5.6 など旧API向け）。一覧には名前・性格と顔画像だけを持つ軽量版を置き、招待する瞬間に完全なカードを読み直す。1枚あたり約30MB・約100枚でHシーン入場ごとに約3GBのメモリ増加を抑える |
| `LazyInviteCardList` | `IncludeMaleCards` | bool | `true` | 男性一覧（kPlug の EnBuddyInvite で出るバディ用カード）も軽量版にし、`Dealer.InviteBuddy` の時点で読み直す（v1.6.0）。無効にすると完全版のまま保持する |
| `LazyInviteCardList` | `PartialLoad` | bool | `true` | 一覧用カードを、最初から全身画像・顔画像・パラメータだけ読む（v1.7.0）。拡張データ（Material Editor のテクスチャなど。1枚あたり数十MB）を読まないので、Hシーン入場時の読み込み時間と一時的なメモリ増加を減らす。読めなかったカードは完全に読む |
| `InviteCard` | `ShowSameNameCardsInInviteList` | bool | `true` | 在席中のキャラ（メインヒロイン・セカンドヒロイン・招待済み）と同名のカードも招待リストに出す（v1.8.2）。対象はカード一覧だけで、ストーリー／デート相手のヒロインは従来どおり除外される |
| `InviteCard` | `EnableVRInviteMenuUnblock` | bool | `true` | kPlug 3.599 以降の VR版で、同じカードを何度でも招待できるようにする（新しい招待UIは招待済みカードを一覧から外すため）。3.59 以前のVR・非VR版では何もしない |
| `InviteCard` | `EnableInviteCardDiagnostics` | bool | `false` | 招待時に Prefix を通ったか、InviteCard に付いているパッチの一覧などをログ出力する診断（v1.5.1。読み取り専用） |
| `SpotCtrlAdaptColDiagnostics` | `EnableSpotCtrlAdaptColDiagnostics` | bool | `false` | kPlug `SpotCtrl.AdaptColSclToPose()` の NRE 発生時に、`ai` / `ai.actionBinder` / `infos` / `spotCol` の状態を記録する診断（読み取り専用・例外は抑止しない） |
| `CoordinateChangeStaleRefDiagnostics` | `EnableCoordinateChangeStaleRefDiagnostics` | bool | `true` | 上記の状態変化を検出時のみログ出力する診断（読み取り専用） |
| `KPlugVRNullGuards` | `EnableVRRootMenuVisibilityGuard` | bool | `true` | kPlugVR `VRCtrl.SetMenuVisibility()` の `VrRootMenu.menuGo` null ガード（VRのみ、Hシーン開始時の NRE 対策）。スキップ開始・解消をログ出力 |
| `KPlugVRNullGuards` | `EnableVRCtrlLateUpdateGuard` | bool | `true` | kPlugVR `VRCtrl.LateUpdate()` が、タイトル用 VRCtrl の自壊フレームで出す NRE のみを抑止（Finalizer、原型の挙動は不変） |
| `KPlugVRNullGuards` | `EnableHinputParserChainGuard` | bool | `true` | kPlugVR `HinputParser.Chain()` を、原型が何もしない／副作用前に NRE になる状態（`Core.hCtrl`・`dealer`・`mainAi`・`VRCtrl`・`leads` の未確定）でのみスキップ。ガード外の NRE は状態を記録して通す |
| `KPlugVRNullGuards` | `EnableAIPublicKbGuard` | bool | `true` | kPlugVR `AI_Public.LateUpdate()` の `bc.kb` null ガード（招待直後の NRE 対策）。下限補正だけを飛ばし残りは実行。非VR版は作者修正済みのため対象外 |
| `KPlugVRNullGuards` | `EnableAIMainFaceColorGuard` | bool | `true` | `AI_Main.Ahegao()` の `Core.heroineCha.customMatFace` null ガード（v1.8.1）。アヘ顔の表示中に毎フレーム出る NRE の対策。頬の色の上書きだけを飛ばし、`Ahegao` 末尾の状態記録と `LateUpdate` の残りは実行する（原型では例外で両方とも飛ぶ）。VR・通常モードの両方が対象 |
| `KPlugVRNullGuards` | `EnableHCtrlKissGuard` | bool | `true` | kPlugVR `HCtrl.ParseTouchables()` の `kissable_ai` null ガード（v1.8.5）。招待キャラへのキスが終わる瞬間の NRE 対策。`KissByPlayer()` の呼び出しだけを飛ばす（メソッドの最後の文）。kPlug 3.63 以降の VR は作者修正済みのため対象外。VRのみ |
| `KPlugVRNullGuards` | `EnableGuestFaceColorGuard` | bool | `true` | kPlug `FaceCtrl.IE_SetFace` の `customMatFace` null ガード（v1.10.3）。アヘ顔中の招待キャラで表情が変わるたびに出る NRE の対策。頬の色の上書きだけを飛ばし、表情の記録まで実行する。どのキャラで起きたかをログ出力。VR・通常モードの両方が対象 |
| `KPlugVRNullGuards` | `EnableAISecondFaceColorGuard` | bool | `true` | `AI_Second.Ahegao()` の `Core.heroine2Cha.customMatFace` null ガード（v1.10.4）。3P の2人目のアヘ顔の表示中に毎フレーム出る NRE の対策。頬の色の上書きだけを飛ばし、`Ahegao` 末尾の状態記録と `LateUpdate` の残り（2人目のまばたき設定など）は実行する。VR・通常モードの両方が対象 |
| `KPlugVRNullGuards` | `EnableCopyLiquidMapGuard` | bool | `true` | `ChaTools.CopyLiquidMapToMat()` のマテリアル null ガード（v1.10.5）。顔の素材の無い顔 MOD のキャラなどで Hシーン開始時に出る NRE の対策。写し元・写し先のどちらかが null のときだけコピーを飛ばして false を返す。VR・通常モードの両方が対象 |
| `KPlugMemoryDiagnostics` | `EnableKPlugMemoryDiagnostics` | bool | `false` | kPlug の招待一覧以外のメモリ消費を調べる診断（v1.6.1。読み取り専用）。InviteUI.Start の前後のヒープ・コミットの変化と、Start 以外で kPlug が全カードを読み込む処理（FindCardData など）の回数・呼び出し元をログ出力する |
| `KPlugPerfDiagnostics` | `EnableKPlugPerfDiagnostics` | bool | `false` | 招待1人ごとのコストと、複数人招待時のフレームレート低下の診断（v1.7.1。読み取り専用）。30秒ごとのフレーム時間・キャラ数・GC・例外の件数、kPlug のメソッド別の処理時間、キャラの読み込みの所要時間、招待前後のメモリをログ出力する。計測自体にわずかな負荷がある |
| `KPlugPerfDiagnostics` | `ProfileKPlugMethods` | bool | `false` | `EnableKPlugPerfDiagnostics` の一部。kPlug の MonoBehaviour の Update / LateUpdate / FixedUpdate / OnGUI（約150個）に処理時間の計測を当てる |
| `KPlugPerfDiagnostics` | `ProfileInviteCoroutines` | bool | `false` | `ProfileKPlugMethods` の一部。招待に関係する kPlug のコルーチンの1回ごとの処理時間も計測する |
| `KPlugPerfDiagnostics` | `ProfileGameCharaUpdates` | bool | `true` | `ProfileKPlugMethods` の一部（v1.7.4）。ゲーム本体のキャラごとの更新処理（DynamicBone、FaceBlendShape、視線・首の制御、服・髪・アクセサリなど）にも計測を当てる。`ProfileKPlugMethods` が無効なら何もしない |
| `KPlugPerfDiagnostics` | `TraceSwapSection` | bool | `false` | キャラ交代で止まる区間の処理の計測（v1.8.0。読み取り専用） |
| `DynamicBoneThrottle` | `DisableInvitedDynamicBones` | bool | `true` | 招待キャラとバディの揺れもの（DynamicBone / Ver01）の計算を止める（v1.8.0）。複数人招待時のフレームレート低下の主因への対策。止めた揺れものは初期の姿勢で静止する。5人時のフレーム時間が 123〜172ms から 28〜37ms になった（通常モードで確認） |
| `DynamicBoneThrottle` | `DisableInvitedBreastDynamicBones` | bool | `false` | 上に胸（DynamicBone_Ver02）も含める |
| `DynamicBoneThrottle` | `LimitDynamicBoneIterations` | bool | `true` | すべてのキャラの揺れもの（DynamicBone / Ver01）の1フレームの計算回数を `MaxIterationsPerFrame` までに抑える（v1.8.0）。元の処理はフレームが遅いと1フレームで最大3回計算するため、フレームレートが下がるほど重くなる。60fps 以上なら見た目は変わらない |
| `DynamicBoneThrottle` | `MaxIterationsPerFrame` | int | `1` | `LimitDynamicBoneIterations` が有効なときの、1フレームあたりの揺れものの計算回数の上限（1〜2。3以上は元の処理と同じ） |
| `HVoiceCachingFix` | `EnableHVoiceCachingShortBreathFix` | bool | `true` | HVoiceCaching 0.3 併用時に、kPlug のキャラ交代のたびに `HVoiceCtrl.Init` の中で出る例外を防ぐ（v1.8.3）。例外で交代処理の後半が飛び、キャラの声の性格が入れ替わる・外に出たキャラが交代中のまま残る問題の対策。HVoiceCaching が無ければ何もしない。**ゲーム本体(Assembly-CSharp)へのパッチ** |
| `HVoiceCachingFix` | `EnableHVoiceCachingVoiceListFix` | bool | `true` | HVoiceCaching 0.3 併用時に、3P（女の子2人）の Hシーンがロード画面のまま進まなくなる問題の回避策（v1.9.1）。HVoiceCaching が1人目だけキャッシュから読んで本来の読み込みに戻したとき、声の辞書を `Init` 直後の状態に戻してから読ませる。HVoiceCaching が無ければ何もしない。**ゲーム本体(Assembly-CSharp)へのパッチ** |
| `SetFaceDiagnostics` | `EnableSetFaceNreDiagnostics` | bool | `false` | kPlug `FaceCtrl.IE_SetFace` の NRE の原因を調べる診断（v1.10.2、読み取り専用）。例外が出たときだけ、キャラ・表情の種類・アヘ顔の状態と、コルーチンがたどる参照の null の有無を記録する。例外は抑止しない |
| `FaceListFix` | `EnableFaceListReloadOnSwap` | bool | `true` | kPlug のキャラ交代で、表情リスト（`HFaceList`）を交代後のキャラの性格で読み直す（v1.8.4）。kPlug は声のリストしか読み直さないため、交代後もHシーン開始時のメインキャラの性格の表情になる問題の対策。招待キャラの表情も今のメインキャラの性格の表になる。**ゲーム本体(Assembly-CSharp)へのパッチ** |
| `MultiGuestKiss` | `EnableMultiGuestKiss` | bool | `true` | 招待キャラ複数との同時キス（v1.9.0）。ほかの招待キャラがキス中のときだけ、下の範囲に入った2人目以降のキスも始める。VRのみ |
| `MultiGuestKiss` | `StartDistance` | float | `0.18` | 2人目以降のキスを始める距離（m、鼻とヘッドセット）。kPlug 3.56 の本来の範囲は 0.12m（KissDist で ±0.1m）（設定できる範囲 0.05〜0.5） |
| `MultiGuestKiss` | `StartAngle` | float | `30` | 2人目以降のキスを始める角度（度、正面からのずれ）。kPlug の本来の範囲は 25°（設定できる範囲 5〜90） |
| `MultiGuestKiss` | `KeepDistance` | float | `0.22` | 同時キスを続ける距離（m）。StartDistance より小さければ StartDistance（設定できる範囲 0.05〜0.6） |
| `MultiGuestKiss` | `KeepAngle` | float | `45` | 同時キスを続ける角度（度）。StartAngle より小さければ StartAngle（設定できる範囲 5〜90） |
| `MultiGuestKiss` | `EnableStuckGuestKissFix` | bool | `true` | kPlug のキスの相手でも同時キスの対象でもないのに 0.5 秒以上キス状態のまま残った招待キャラのキスを終わらせる（キスの相手が直接替わると kPlug が前の相手の FinishKiss を呼ばない問題）。VRのみ |

同名キャラの複数招待（改名処理）には設定項目はなく、常に有効です。

## 8. 配布・ライセンス・免責

- **ライセンスは MIT です**（[LICENSE](LICENSE)）。v1.11.0 から、これまでの「配布・修正は自由、ソースとビルドスクリプトを同梱」の条件に代えて MIT にしました。kPlug の作者が修正を kPlug に取り込みやすくするためです。
- **本ソフトは無保証で提供されます。** 利用によって生じたいかなる損害（セーブデータの破損、ゲームの不具合、他プラグインとの競合などを含む）についても、作者は責任を負いません。自己責任でご利用ください。
- **kPlug は別の作者による著作物であり、本アーカイブには含まれていません。** 本プラグインは kPlug の不具合を外から回避するためのパッチであり、kPlug 本体を配布・改変するものではありません。kPlug 本体は配布元から入手してください。ゲーム本体・BepInEx・その他のプラグインのファイルも同梱していません。
- 本プラグインは kPlug の動作を実行時に書き換えます。**kPlug のバージョンが変わると対象のメソッドが見つからず、機能の一部または全部が無効になることがあります**（その場合はログに理由が出ます）。

---
作成日時: 2026年6月11日 / 最終更新: 2026年10月20日 (v1.11.2)
作成者: Gemini CLI / Claude Opus 5
