# kPlug issues found while making kPlugPatcher_InviteChrID

This document is for the author of kPlug / kPlugVR (Katarsys).
It lists the problems that kPlugPatcher_InviteChrID works around, with the exception stacks, the cause, and a suggested fix inside kPlug.
The goal is to make it easy to fix them in kPlug itself, so that this patch is no longer needed.

- No kPlug code is included here or in the patch. Types, methods and fields are referred to by name only. The suggested fixes are short sketches written for this document.
- Names are given for kPlug (`kPlug.*`). kPlugVR (`kPlugVR.*`) has the same code unless noted.
- Versions: most issues were found and tested with **kPlug 3.5.6** (normal and VR). The patch was then run with **kPlug 3.6.3** (including the 2026-10-19 update, normal and VR).
  For each issue, "3.6.3" says whether the same code is still there, and whether we saw the problem in a 3.6.3 log.
  #14–#17 were found later with kPlug / kPlugVR 3.6.3 (2026-10-21).
- Stack traces come from real play logs. Koikatu's Mono does not print IL offsets for methods that are not patched (`[0x00000]`), so most stacks below have none.
  Where an offset is given, it was checked against the IL of that build.
- Games: Koikatu and Koikatsu Party with BepInEx 5, normal and VR. The test setup has a few hundred character cards and many mods.

## Summary

| # | Where | What the player sees | Type | 3.6.3 | Patch file / config key |
|---|---|---|---|---|---|
| 1 | `CmpH.SpotManager.AdaptCaliber` → `Dealer.InProcess` | Invite does nothing for the rest of the H scene | stale reference + lock | same code | `SpotManagerBonesFix.cs` / `EnableSpotManagerBonesFix` |
| 2 | `Tools.ChaTools+ColDefsManager.LateUpdate`, `CmpH.BindersCtrl.Update`, `CmpChara.BonesCtrl.LateUpdate` | NRE every frame after a coordinate reload (thousands per H) | stale reference | same code | `CoordinateChangeStaleRefFix.cs` / `EnableCoordinateChangeStaleRefFix` |
| 3 | `CmpChara.BellyCtrl` after a swap | NRE every frame, belly inflation stops; one swap value uses the wrong bone | stale reference + typo | same code, seen in 3.6.3 VR | `CoordinateChangeStaleRefFix.cs` / `EnableBellyCtrlStaleBoneFix` |
| 4 | `AI_Main.Ahegao`, `AI_Second.Ahegao`, `FaceCtrl.IE_SetFace`, `ChaTools.CopyLiquidMapToMat` | NRE every frame in ahegao for characters without a face material | missing null check | same code | `KPlugVRNullGuards.cs` / 4 keys (see §4) |
| 5 | `Tools.ToolExtrAnimIK.SetIkTargets` | IK targets set to `null`; the game's `MotionIK.LinkIK` throws and the orgasm repeats | missing null check | same code, seen in 3.6.3 VR | `MotionIKNullTargetGuard.cs` / `EnableExtraAnimIkTargetGuard`, `EnableMotionIKNullTargetGuard` |
| 6 | `AI_Public.LateUpdate` (VR), `AI_Public.WatchAnOrgasm` | One NRE per invited girl | `bc.kb` not set yet | VR: same code, seen in 3.6.3 | `KPlugVRNullGuards.cs` / `EnableAIPublicKbGuard` (LateUpdate only) |
| 7 | Same-name cards | Inviting the same card twice breaks leave / name-based actions; a card with the main girl's name is not listed | names used as keys | same code | `InviteCardRename.cs` (always on), `InviteListSameNameUnblock.cs` / `ShowSameNameCardsInInviteList` |
| 8 | Character swap | After a swap, faces do not match the lines | face list not reloaded | same code | `FaceListReloadOnSwap.cs` / `EnableFaceListReloadOnSwap` |
| 9 | Invite menu (`InviteUI.Start`) | About 3 GB of memory per H scene (100 cards) | memory | normal: same code | `LazyInviteCardList.cs` / `EnableLazyInviteCardList` |
| 10 | VR kiss with invited girls (`HCtrl.ParseTouchables`) | A girl can stay in kiss state; only one girl at a time | behavior | VR: not checked in 3.6.3 | `MultiGuestKiss.cs` / `EnableStuckGuestKissFix`, `EnableMultiGuestKiss` |
| 11 | kPlugVR 3.5.6 only: `VRCtrl.SetMenuVisibility`, `VRCtrl.LateUpdate`, `HinputParser.Chain`, `HCtrl.ParseTouchables` | One NRE at H start / per kiss | missing null check | **fixed or removed in 3.6.3** | `KPlugVRNullGuards.cs` (skipped automatically on 3.6.3) |
| 12 | Game `ParentObjectCtrl`, game `MotionIK`, DynamicBone, HVoiceCaching | Orgasm loop, frame rate, H scene stuck at loading | not kPlug (triggered or made worse by kPlug) | — | §12 |
| 13 | Smaller NREs (not patched) | One exception each time | kPlug, mostly missing null checks | see §13 | not patched |
| 14 | kPlugVR 3.599+ VR invite menu (`MenuInvite.InitFileList`) | +11–13 GB at every VR character select (250 cards); out of memory the second time | memory | found in 3.6.3 VR | `LazyInviteCardList.cs` / `EnableVRMenuInvite` |
| 15 | Invite menu face textures (`InviteUI.Start`, `MenuInvite.GetChaInfoList`) | Hundreds of MB of GPU and CPU memory; normal mode: kept after H, even on the title screen | memory | found in 3.6.3 | `InviteUIFaceShrink.cs`, `MenuInviteFaceShrink.cs` / `ShrinkInviteMenuFaces`, `ReleaseInviteMenuFaces`, `ShrinkVRMenuFaces` |
| 16 | kPlugVR 3.599+ VR menus (world-space Canvas) | Transparent clothes behind a menu are drawn over it; the invite menu flickers | draw order | found in 3.6.3 VR | `VRMenuFlickerDiagnostics.cs` / `DrawMenusOnTop`, `Method` |
| 17 | `CmpChara.KokanBehavior` | Unity warning "Infinity or NaN … for a Collider" every frame during insertion (tens of thousands per session) | division by frame time | found in 3.6.3 VR | `KokanNaNGuard.cs` / `EnableKokanNaNGuard` |

## Common cause of #1–#3 (and of several in #13)

Several components look up Transforms or GameObjects of a character's body once, at initialization, and keep them for the whole H scene.
When the body is rebuilt (`ChaControl.Reload` / `ChangeCoordinateTypeAndReload`, e.g. a coordinate change at the start of H, or a character swap), Unity destroys those objects and creates new ones with the same names.
The cached C# references are not null, but they point to destroyed objects, so the next `SetActive`, `localPosition` or `position` call throws `NullReferenceException`.

In the logs the first exceptions come right after a coordinate reload of that character (`CoordinateChanged`), for example when an invited girl is dressed, and after a character swap.
Whose references break is simple: the character that was reloaded.

Useful Unity facts for the fix:
- `obj == null` (Unity's overloaded operator) is `true` for destroyed objects. `obj is null` / `ReferenceEquals(obj, null)` is not.
- `UnityEngine.Object.Equals` treats all destroyed objects as equal, so do not use destroyed objects as dictionary keys without a reference comparer.

A general fix is either to check the cached object before use and find it again by name, or to refresh the cache when the character reloads.

## 1. Invite does nothing (`SpotManager.AdaptCaliber` and `Dealer.InProcess`)

Stack (kPlugVR 3.5.6; the same happens in kPlug):

```
NullReferenceException
  at (wrapper managed-to-native) UnityEngine.Transform:INTERNAL_get_position (UnityEngine.Vector3&)
  at UnityEngine.Transform.get_position ()
  at kPlugVR.CmpH.SpotManager.AdaptCaliber ()
  at kPlugVR.CmpH.SpotManager.CreateCaliber ()
  at kPlugVR.CmpH.SpotManager.Start ()
```

**What the player sees.** Clicking Invite does nothing, with nothing in the log, for the rest of the H scene. It happens with some main girls and not others, and not every time.

**Cause.**
- The first invite in an H scene sets `Dealer.InProcess = true` and adds `SpotManager`. `SpotManager.Start` → `CreateCaliber` → `AdaptCaliber` reads bone positions of the main girl's `BonesCtrl` (`bcG`).
- In our case `bcG.spine3` was a **destroyed** Transform (not null): `FindBones` had found it, and the bone hierarchy was rebuilt later. We logged about 30 fields before `AdaptCaliber`; between a failing and a working run, `spine3` was the only difference.
- The exception ends `SpotManager.Start` before it starts `IE_CreateSpots`, so `spotsAreInit` never becomes `true`.
  The invite coroutine waits for `spotsAreInit` forever, and `InProcess = false` is only set at its end. Every later click is ignored by the `if (!InProcess)` check, without a log line.

**Suggested fix.**
- Before using the `BonesCtrl` bones in `AdaptCaliber`, check them with Unity's `== null`; if any is missing, call `BonesCtrl.FindBones()` again (or skip the caliber step for this frame).
- Make the invite flow robust: if `SpotManager` fails to init, reset `InProcess` (e.g. a timeout, or a `try`/`finally` around the part that sets it).

**Patch.** A prefix on `AdaptCaliber` checks the main girl's `BonesCtrl` fields and calls kPlug's own `BonesCtrl.FindBones()` when one is destroyed.

## 2. `ColDefsManager.LateUpdate`, `BindersCtrl.Update`, `BonesCtrl.LateUpdate`

Stacks (every frame until the end of the H scene; up to 71,050 `ColDefsManager` and 35,525 `BonesCtrl` exceptions in one VR session, about 3,000–5,000 of each in normal sessions):

```
NullReferenceException
  at (wrapper managed-to-native) UnityEngine.GameObject:SetActive (bool)
  at kPlug.Tools.ChaTools+ColDefsManager.LateUpdate ()

NullReferenceException
  at (wrapper managed-to-native) UnityEngine.GameObject:get_activeSelf ()
  at kPlug.CmpH.BindersCtrl.Update ()

NullReferenceException
  at (wrapper managed-to-native) UnityEngine.Transform:INTERNAL_get_localPosition (UnityEngine.Vector3&)
  at UnityEngine.Transform.get_localPosition ()
  at kPlug.CmpChara.BonesCtrl.LateUpdate ()
```

**Cause.** One cause, three places (we logged the state at each change):
- `ColDefsManager`: `gos` only grows (`GetOrCreate`); nothing removes destroyed GameObjects. `LateUpdate` calls `SetActive` on every entry and throws on the first destroyed one.
- `BindersCtrl`: `binders` is shared by all characters. After a reload, the `Binder.root` of that character's binders is destroyed, and `Update` reads `root.activeSelf`.
- `BonesCtrl`: after a reload of that character, `root`, `waist`, `spine1`–`spine3`, `neck` and `head` were all destroyed at once (arms, legs and `hipShaper_L/R` were not). `LateUpdate` uses all of them, and the `customs` list, without a check.
  (`BonesCtrl.nose` / `eyeBridge` can break the same way; then `AI_Public.IE_CalcDistFromCam` and `FaceCtrl.LateUpdate` throw too.)

**Suggested fix.**

```csharp
// ColDefsManager.LateUpdate
gos.RemoveAll(go => go == null);                 // Unity's == null also matches destroyed objects

// BindersCtrl.Update
binders.RemoveAll(b => b == null || b.root == null);

// BonesCtrl.LateUpdate (start)
if (root == null || waist == null || spine1 == null /* ... */) FindBones();
customs.RemoveAll(c => c == null || c.trs == null);
```

`FindBones()` should not be called every frame if the bones are not there yet (the body may still be loading); a short wait between tries is enough.
Wait only after a try that did not fix them. The same character can be reloaded again on the very next frame (clicking the coordinate button quickly), and a wait after a successful repair gives an NRE on every frame of the wait.

**Patch.** Prefixes do exactly the above. `FindBones()` is retried at most 3 times, 60 frames apart, only while a try does not fix them; a successful repair resets the wait (1.11.2), so a reload right after a repair is fixed at once. The core bones are checked every frame, the others every 10 frames.

## 3. `BellyCtrl` after a character swap

Stack (kPlugVR 3.6.3, 2,376 times in one session, every frame after the first swap):

```
NullReferenceException
  at (wrapper managed-to-native) UnityEngine.Transform.INTERNAL_set_localPosition (UnityEngine.Vector3&)
  at UnityEngine.Transform.set_localPosition (UnityEngine.Vector3)
  at kPlugVR.CmpChara.BellyCtrl.LerpInflation (kPlugVR.CmpChara.BellyCtrl/Bone,UnityEngine.Vector3,UnityEngine.Vector3,UnityEngine.Vector3,single) <IL 0x0002c>
  at kPlugVR.CmpChara.BellyCtrl.LateUpdate ()
```

**Cause.**
- `BellyCtrl` finds `cf_s_waist02`, `cf_s_waist01`, `cf_s_spine01` and `cf_s_spine02` once (`GetBone`) and stores them in `Bone.t`. Nothing finds them again.
- `AI_Public.IE_SwapGirls` reloads both characters and then calls `BellyCtrl.SwapBellyValues(main, guest)`. That swaps the base values (`locPos/Rot/Scl`, `bustSize`, `bulge`) but keeps the old, now destroyed, `t`.
- When the pregnancy setting is on, `LateUpdate` calls `LerpInflation` for the four bones every frame. It throws on the first one, so the other three bones and the rest of `LateUpdate` (inflation growth, decrease) stop too.

**A second bug in `SwapBellyValues`.** Its second line swaps `waist2` of the first character with **`waist1`** of the second one. It is most likely meant to be `waist2` of both. Every swap mixes the base values of the two bones (normal and VR builds).

**Suggested fix.**
- In `LateUpdate` (or right after `SwapBellyValues`), if a `Bone.t` is destroyed, find the bone by name in the character again and replace only `t`; keep the base values.
- Fix the `waist2` / `waist1` pair in `SwapBellyValues`.

**Patch.** A prefix on `LateUpdate` re-finds destroyed bones with kPlug's own `ChaTools.FindInCha` (3 tries, 60 frames apart while a try does not fix them; reset after a successful repair); a prefix on `LerpInflation` skips a bone whose `t` is missing. The `SwapBellyValues` typo is **not** changed by the patch.

## 4. Face material is used without a null check

Stacks:

```
NullReferenceException                                   (9,109 times in one VR session, 2,202 in a normal one)
  at kPlug.CmpH.AI_Main.Ahegao ()
  at kPlug.CmpH.AI_Main.LateUpdate ()

NullReferenceException                                   (18,013 times in one VR session, 3P)
  at kPlugVR.CmpH.AI_Second.Ahegao ()
  at kPlugVR.CmpH.AI_Second.LateUpdate ()

NullReferenceException                                   (477 times in one VR session, 86 in a normal one)
  at kPlug.CmpChara.FaceCtrl+<IE_SetFace>d__83.MoveNext ()

NullReferenceException                                   (1–4 times at the start of H)
  at kPlugVR.Tools.ChaTools.CopyLiquidMapToMat (UnityEngine.Material mat, Boolean isFace, Vector2 liquidScl, Vector2 liquidOfst, Boolean includeMask)
  at kPlugVR.CmpChara.DickBehavior.SetLiquidOnBodyMat ()
  at kPlugVR.CmpChara.DickBehavior.Start ()
```

**Cause.** `ChaControl.customMatFace` can be `null`. It happens with head mods whose list entry has no face material (`MatAB` = 0), or whose material name is the same as the head object name, so the game's `LoadAsset<Material>` finds nothing.
In our setup, 25 of 99 head mods by one author have no face material. The game itself works with such heads; kPlug uses the material without a check:
- `AI_Main.Ahegao` / `AI_Second.Ahegao`: `Core.heroineCha.customMatFace` / `Core.heroine2Cha.customMatFace` → `SetColor` (cheek color) every frame while in ahegao.
  The exception also skips the state update at the end of `Ahegao` and the rest of `LateUpdate` (for `AI_Second`: the blink setting and the bukkake mask).
- `FaceCtrl.IE_SetFace` (invited girls): `customMatFace.SetColor` in ahegao, each time the face changes. The exception skips the "current face" record at the end.
- `ChaTools.CopyLiquidMapToMat`: the source (`Core.heroineCha.customMatFace` / `customMatBody`) and the target `mat` are used without a check. The exception skips the rest of `DickBehavior.Start`.

**Suggested fix.** Check the material before use and skip only that call:

```csharp
Material face = Core.heroineCha.customMatFace;
if (face != null) face.SetColor(/* cheek color */);

// CopyLiquidMapToMat
if (mat == null || source == null) return false;   // "nothing copied"
```

**Patch.** Transpilers replace only the `customMatFace.SetColor` call with a version that does nothing when the material is null (`EnableAIMainFaceColorGuard`, `EnableAISecondFaceColorGuard`, `EnableGuestFaceColorGuard`).
A prefix on `CopyLiquidMapToMat` returns `false` when the source or the target is null (`EnableCopyLiquidMapGuard`). All four apply to kPlug and kPlugVR 3.5.6 and 3.6.3.

## 5. `ToolExtrAnimIK.SetIkTargets` sets IK targets to null

Stack (kPlugVR 3.6.3, with KK_SetParentVR moving the male; 1 exception in kPlug and 65 in the game in one session):

```
NullReferenceException
  at kPlugVR.Tools.ToolExtrAnimIK/IkHook.Create (UnityEngine.Transform,UnityEngine.Transform,UnityEngine.Transform) <IL 0x00001>
  at kPlugVR.Tools.ToolExtrAnimIK.SetIkTargets (ChaControl,string,string,bool)
  at kPlugVR.CmpH.ExtraAnimManager.SetIkTargets (bool)
  at kPlugVR.CmpH.ExtraAnimManager.CheckOnStateChange ()
  at kPlugVR.Core.HActionBase_PostSetPlay (string,bool)
  at HActionBase.SetPlay (HActionBase,string,bool)
  at HSonyu.LoopProc (bool)
```

After that, the game's `MotionIK.LinkIK` throws at every animation change (`HSonyu.Proc` → `HActionBase.SetPlay` → `MotionIK.Calc` → `MotionIK.LinkIK`).

**Cause.**
- On each animation change, `SetIkTargets` finds the hand and foot IK targets in the character by name (`ChaTools.FindInCha(cha, "cf_t_hand_L")` and so on) and assigns the result to `iKEffector.target` **without checking for null**. `IkHook.Create` also uses its first argument without a check.
- When the target cannot be found, the effector of the game's `MotionIK` (shared with kPlug) now has a null target. In our case the male's body bones were outside his `ChaControl` for a moment (KK_SetParentVR moves them while the player moves the male), but any case where `FindInCha` returns null has the same effect.
- The game's `MotionIK.LinkIK` then throws inside `HActionBase.SetPlay`. In `HSonyu.Proc` this skips the code after `SetPlay` (`flags.finish = none`, `gaugeMale = gaugeFemale = 0`), so the orgasm repeats ("orgasm loop"). kPlug's postfix that would set the targets again is never reached, so it keeps happening.

**Suggested fix.** In `SetIkTargets`, only assign a target that was found; keep the previous one otherwise. In `IkHook.Create`, return when the hook target is null.

**Patch.** A prefix/finalizer pair on `SetIkTargets` restores any target that kPlug set to null; `IkHook.Create` is skipped for a null target. A game-side safety net skips only the limb with a null target in `MotionIK.LinkIK`.

## 6. `bc.kb` is used before `BonesCtrl.Start` runs

Stacks:

```
NullReferenceException                                   (kPlugVR, about once per invited girl; 148 in 39 VR logs)
  at kPlugVR.CmpH.AI_Public.LateUpdate ()

NullReferenceException                                   (kPlug 3.5.6 normal, IL 0x50; kPlugVR 3.6.3, IL 0x21: bc.kb.min_exitation)
  at kPlug.CmpH.AI_Public.WatchAnOrgasm (single)
  at kPlug.CmpChara.DickBehavior.LateUpdate ()
```

**Cause.** `AI_Public.Start` creates `bc` with `BonesCtrl.Create` (`AddComponent`). `bc.kb` is set only in `BonesCtrl.Start`, which Unity runs later (before the component's first update), not inside `AddComponent`.
In the same frame, `AI_Public.LateUpdate` reads `bc.kb.min_exitation`. We logged it: the null window was exactly one frame for every spawned girl, and the count of exceptions matched the count of spawns.
The normal build of `AI_Public.LateUpdate` already checks `bc != null && bc.kb != null`; the VR build does not (3.5.6 and 3.6.3).
`WatchAnOrgasm` reads `bc.kb.min_exitation` without the check in both builds; it throws when an orgasm happens in that first frame of a newly invited girl.

**Suggested fix.** Use the same check as the normal `LateUpdate` in kPlugVR's `LateUpdate` and in `WatchAnOrgasm` (both builds), or set `kb` in `BonesCtrl.Create`.

**Patch.** A transpiler on kPlugVR `AI_Public.LateUpdate` skips only the `min_exitation` lower-bound line while `bc.kb` is null. `WatchAnOrgasm` is not patched.

## 7. Same-name cards

**What the player sees.**
- Inviting the same card twice: the girls have the same name, so leaving, name-based actions and the present-girl list mix them up (they are not loaded properly, or the wrong one leaves).
- A card with the same name as the main girl (or a girl already invited) is not shown in the invite list at all (`InviteUI.IsAlreadyInHarem` compares names).

**Cause.** The character name is used as the key for "who is present", for the spawned character, the display-name dictionary and the name-based API.
(Normal build: `InviteUI.PresentNames`. VR build: built from `mainAi` / `secondAi` / `harem` each time.)

**Suggested fix.** Use a per-instance key (for example a counter or the `ChaControl` instance) instead of the name, or give the copy a unique name at invite time, like the patch does.

**Patch.** At `Dealer.InviteCard` (VR 3.599 and later: `Dealer.InviteInVR`), the patch copies the card data in memory and renames only the copy ("Name" → "Name (1)", "Name (2)"). The card file and the card in the invite list are not changed.
Story / date heroines from the save data are never renamed. A second patch removes only the name check from the invite list (cards only; save-data heroines are still hidden).
For kPlugVR 3.599 and later, the new invite menu (`Menu.MenuInvite.InviteSomeone`) also removes an invited card from the list; the patch keeps it there (`EnableVRInviteMenuUnblock`).

## 8. A swap reloads the voice list but not the face list

**Cause.** H faces come from a per-personality table (`HFaceList`): each line has a face number, and the table turns it into eyebrows, eyes and mouth.
kPlug's swap calls `HVoiceCtrl.Init` with the new girl's personality (voice list) but does not reload the face list. After a swap the new main girl uses the face table of the personality the H scene started with.
The face numbers mean different faces in each personality, so the faces do not match the lines. Invited girls use the main girl's table too.
Checked in kPlug 3.5.6 and 3.6.3, normal and VR.

**Suggested fix.** When the swap reloads the voice list for another personality, also reload the face list for that personality (keep the old one if it is not found).

**Patch.** A postfix on `HVoiceCtrl.Init` reloads the face list when the personality changed (not at the start of H, where the game already loaded it).

## 9. Invite menu loads every card fully and keeps it

**What the player sees.** Memory grows by about 30 MB per card at every H scene (100 cards: about 3 GB). In our normal-mode tests the Mono heap peaked at 5.4 GB; building the invite menu took about 12 s and +2 GB of committed memory.

**Cause.** `InviteUI.Start` loads every card in the folder as a full `ChaFileControl` (including extended data such as Material Editor textures) and keeps them for the whole H scene, although the list only needs the name, personality and face image.
The same happens for the male list (buddy invite).

**Suggested fix.** Keep only the card path, name, personality and face image in the list, and load the full card when the player invites it.

**Patch.** The list gets a light card (full-body image, face image and parameters only, read directly from the file); the full card is loaded at `Dealer.InviteCard` / `InviteBuddy`.
Result in normal mode: Mono peak 5.4 GB → 1.75 GB; menu build about 1 s and +410 MB. kPlugVR 3.599 and later use a new invite menu in VR; see §14.

## 10. VR kiss with invited girls (`HCtrl.ParseTouchables`)

- If the kiss partner changes from one invited girl to another in the same frame, the previous girl does not get `FinishKiss` and stays in kiss state. The patch ends the kiss of an invited girl that has been in kiss state for 0.5 s without being the partner (`EnableStuckGuestKissFix`). Seen with kPlugVR 3.5.6.
- Only the first girl in range can be kissed. The patch adds an option to kiss several invited girls at the same time (`EnableMultiGuestKiss`). This is a feature, not a bug; it is listed here because it shares the code above.

## 11. kPlugVR 3.5.6 only (fixed or removed in 3.6.3)

| Where | Cause | 3.6.3 |
|---|---|---|
| `VRCtrl.SetMenuVisibility` (once at H start) | `VrRootMenu.menuGo` is used without the null check that the other menus have | `VRCtrl` removed |
| `VRCtrl.LateUpdate` (once at start) | runs in the frame where the title `VRCtrl` destroys itself | `VRCtrl` removed |
| `HinputParser.Chain` (a few times at H start) | `Core.hCtrl`, `dealer`, `mainAi`, `VRCtrl` or `leads` not set yet | removed |
| `HCtrl.ParseTouchables` (about once per guest kiss, 63 in 10 logs) | `kissable_ai.KissByPlayer()` is called after the girl left the kiss range (`kissable_ai` is null, `inAiKiss` still true) | **fixed** (null check added) |

The patch checks the IL and does nothing on versions where the method is gone or already checked.

## 12. Not kPlug, but triggered or made worse by kPlug

- **Game `ParentObjectCtrl` (orgasm loop).** `ParentObjectCtrl.Visible` / `Proc` (H items parented to bones) call `SetActive` on destroyed GameObjects after a body reload, while `ReleaseObject` of the same class checks for null.
  The exception is inside `HActionBase.SetPlay`, so `HSonyu.Proc` never resets `gaugeFemale` and the orgasm repeats. The reloads in H come from coordinate changes and kPlug's swaps.
  The patch replaces both methods with the same logic that skips destroyed items (`ParentObjectCtrlStaleRefFix.cs`). It might be worth shipping the same guard in kPlug.
- **Game `MotionIK.LinkIK`** (§5): the same kind of failure inside `SetPlay`.
- **DynamicBone with many invited girls.** With 5 girls, DynamicBone took about 60% of the frame time; it also runs up to 3 times per frame when the frame is slow, which makes it worse. The patch stops DynamicBones of invited girls and buddies and caps all DynamicBones to 1 update per frame (5 girls: 123–172 ms → 28–37 ms per frame, normal mode).
  A kPlug option to freeze the hair/skirt bones of invited girls would do the same.
  Separately, the game's DynamicBone recomputes the world position and radius of every collider for every particle, so the cost grows with "particles × colliders" (about 0.6 µs per pair; a hair with 233 particles and 41 colliders took about 6 ms per frame).
  The patch computes them once per DynamicBone update and reuses them (`DynamicBoneColliderCache.cs`; same results, 0 differences when compared in normal mode and VR). This is game code; nothing for kPlug to change.
- **HVoiceCaching 0.3** (third-party plugin): kPlug's swap calls `HVoiceCtrl.Init` again, which made HVoiceCaching throw (duplicate keys) and stop the rest of `IE_SwapGirls`; and a 3P H scene could stay on the loading screen. Fixed in the patch (`HVoiceCaching*Fix.cs`); nothing for kPlug to change.

## 13. Smaller NREs (not patched)

These were seen only a few times in our logs (159 logs from 2026-09-26 to 2026-10-19), so the patch does not touch them.
"IL" means the location was checked against the IL of that build; otherwise the cause comes from reading the code around the only reference that can fail.

### 13.1 kPlugVR `ToolSpot.IE_SuspendColliderInInit`

kPlugVR 3.5.6, 39 times in 6 logs (up to 17 in one), right after the H start or an invite:

```
NullReferenceException: Object reference not set to an instance of an object
  at kPlugVR.Tools.ToolSpot+<IE_SuspendColliderInInit>d__23.MoveNext ()
```

**Cause.** The coroutine turns off the colliders of a character, waits for `spotsAreInit`, then sets `enabled = true` on each of them. If one was destroyed while waiting (e.g. a coordinate reload), the loop throws and **the colliders after it stay disabled**.
The normal build already wraps this loop in `try`/`catch`; the VR build does not (3.5.6 and 3.6.3).

**Suggested fix.** In the VR build, skip destroyed colliders (`if (item != null) item.enabled = true;`) or use the same `try`/`catch` as the normal build.

`SpotCtrl.IE_ForceCollider` (normal 3.5.6, once, right after a swap) fails the same way on `Collider.set_enabled`.

### 13.2 `FaceCtrl.IE_PlayVoice`

kPlug and kPlugVR 3.5.6, 26 times in 11 logs (in August, up to 198 in one VR log):

```
NullReferenceException: Object reference not set to an instance of an object
  at kPlug.CmpChara.FaceCtrl+<IE_PlayVoice>d__86.MoveNext ()
```

**Cause (most likely; no IL offset in these logs).** After waiting two frames, the coroutine reads `vSrc.clip.length`. If the voice object was destroyed in between (or has no clip), this throws, and that line's face change (`SetFace`) is skipped. In one log a coordinate reload of the girl came right before it.

**Suggested fix.** `if (vSrc == null || vSrc.clip == null) yield break;` before reading the length.

### 13.3 `SpotCtrl.AdaptColSclToPose`: `ai` is null

kPlugVR 3.5.6, 19 times in 7 logs, always a few lines after `Unloading N unused Assets`:

```
NullReferenceException: Object reference not set to an instance of an object
  at kPlugVR.CmpH.SpotCtrl.AdaptColSclToPose ()
  at kPlugVR.CmpH.SpotCtrl.CustomMove ()
  at kPlugVR.CmpH.SpotCtrl.Update ()
```

**Cause.** We logged the fields at the moment of the exception: `ai` was null every time (`infos` and `spotCol` were fine). `AdaptColSclToPose` reads `ai.InActionBind` and `ai.actionBinder.targetAnim` without a check. We did not find why `ai` is null there (it does not follow guest swaps).
Same code in kPlug and kPlugVR 3.6.3.

**Suggested fix.** `if (ai == null || spotCol == null) return;` at the start, and check `ai.actionBinder` before reading `targetAnim`.

### 13.4 kPlugVR 3.6.3 `SpotCtrl.Update` right after an invite

kPlugVR 3.6.3 only, 12 times in 6 logs: about once per invited girl, right after the invite.

```
NullReferenceException: Object reference not set to an instance of an object
  at kPlugVR.CmpH.SpotCtrl.Update ()
```

**Cause (most likely).** In this build `Update` reads `ai.ikCtrl.inEditPos` when `ai != null`. `SpotCtrl.AttachChara` sets `ai` before the girl's `AI_Public.Start` runs, and `ikCtrl` is set in `AI_Public.Start`, so for one frame `ai.ikCtrl` is null.
(The method was wrapped by our profiler in these logs, so the IL offset is shifted; after removing the shift it points to this expression. It is the same one-frame window as §6.)

**Suggested fix.** `ai != null && ai.ikCtrl != null && !ai.ikCtrl.inEditPos`.

Related: `ToolMisc.AveragePos` from `SpotCtrl.OffsetFromRoot` throws when `ai.bc` leg bones are destroyed (once in normal 3.5.6, once in VR 3.6.3, after a body reload; see "Common cause").

### 13.5 kPlug 3.6.3 `Core.Update` when the free H select scene starts

kPlug 3.6.3 (normal) only, twice each time the free H select scene (`FreeH`) is entered:

```
NullReferenceException: Object reference not set to an instance of an object
  at kPlug.Core.Update ()
```

**Cause.** 3.6.3 added a record of the free H selection (`hSelInf`). `hSelInf` is created by its static field initializer, so the first frame already goes to the "compare with the previous values" branch and reads `FreeHScene.member.resultTimeZone.Value`, `resultStage1.Value` and so on.
The game's `FreeHScene.Start` creates `member` in a field initializer, but creates the `member.result*` properties only after it has waited for the map list. Until then they are null.
(No IL offset in these logs. This code is new in 3.6.3, and the exception appears only in 3.6.3 logs.)

**Suggested fix.** Also check `member.resultTimeZone != null` (and the other `result*` properties) before reading `.Value`.

### 13.6 `Judge.ReachExtase`: `hCount` is null

kPlug 3.5.6 (normal, story mode), 5 times in 4 logs:

```
NullReferenceException: Object reference not set to an instance of an object
  at kPlug.GirlBrain.Judge.ReachExtase (string) <IL 0x00097>
  at kPlug.CmpBase.StatManager.InAhegao (string,bool)
  at kPlug.CmpChara.DickBehavior.LateUpdate ()
```

**Cause (IL).** At 0x97, `hCount.kiss` is read; `Core.statManager.GetHCount(girlName)` returned null. `StatManager.InAhegao` checks `stat` and `daily` for null before calling `ReachExtase`, but not `hCount`. The exception ends the caller's `LateUpdate` (`DickBehavior` or `KokanBehavior`) for that frame.
Same code in 3.6.3.

**Suggested fix.** In `ReachExtase`, return `false` when `hCount == null` (or check it in `InAhegao` like `stat` and `daily`).

### 13.7 `Dealer.IE_SearchNPC` in story mode

kPlug 3.5.6 (normal, ADV and date scenes), 11 times in 7 logs:

```
NullReferenceException: Object reference not set to an instance of an object
  at kPlug.CmpBase.Dealer+<IE_SearchNPC>d__177.MoveNext ()
```

**Cause (most likely).** In each case the exception comes right after the coroutine printed ` IE_SearchMorningNPC() : <name>` for an NPC, so it is the next line: `nPC.chaCtrl.GetComponent<CtrlNpc>()` with `chaCtrl` null (an NPC whose character is not loaded).
Same code in 3.6.3.

**Suggested fix.** `if (nPC == null || nPC.chaCtrl == null) continue;` at the start of the loop (and the same for `fixChara.chaCtrl`).

### 13.8 `MainPoseCtrl.IE_BoyHandTarget` when the player's body is not under his `ChaControl`

kPlugVR 3.6.3, 7 times in one log, while KK_SetParentVR moved the male (same situation as §5):

```
NullReferenceException: Object reference not set to an instance of an object
  at kPlugVR.CmpH.MainPoseCtrl+<IE_BoyHandTarget>d__43.MoveNext ()
```

**Cause.** `ChaTools.GetBodyIkSolver(player)` finds the `FullBodyBipedIK` whose body bone has the player's `ChaControl` as a parent. While the bones are moved outside, it returns null, and the coroutine reads `bodyIkSolver.solver` without a check.
Same code in kPlug and kPlugVR 3.6.3. Only seen with another plugin that moves the body, so it may not matter in normal play.

**Suggested fix.** `if (bodyIkSolver == null) yield break;`

### 13.9 Seen once (not investigated)

| Exception | Build | Note |
|---|---|---|
| `GirlBrain.GirlQA.AllowHardTease (SaveData/Heroine)` IL 0x01, from `HCtrl.ParseTouchablePublic` | kPlug 3.5.6 | the heroine argument is null |
| `CmpH.IksCtrl.ResetHandle` IL 0x46, from `IksCtrl.SnapRoots` ← `LoadIk` ← `SpotCtrl.IE_LoadIk` | kPlug 3.5.6 | `Ctrl.handle` is null |
| `CmpBase.AtHomeCtrl.OnDestroy` | kPlug 3.5.6 | right after ` Dealer : Destroyed, reset spentDay ...` |
| `CmpBase.AdvControl.<IE_init>` (the `WaitUntil` lambda) | kPlug 3.5.6 | ADV |
| `IndexOutOfRangeException` in `Tools.ToolMisc.GetStringAtIndex` IL 0x14, from `DefAndParse.kVoices.GetVoice` ← `FaceCtrl.Speak` | kPlugVR 3.5.6 | the split string has fewer parts than the index |

### 13.10 Exceptions right after leaving H

With 3.5.6, at about 2 of 3 H scene exits, many kPlug components (`InviteUI.OnGUI`, `KokanBehavior`, `AI_Main.TakeControlOnNeck`, `HComboCtrl`, `ToolSpot.GetPlayerDir` …) threw for a few frames after the H scene was unloaded.
With 3.6.3 we saw it once in 7 exits (3 frames). It looks mostly fixed; it is listed only for completeness.

## 14. kPlugVR 3.599+: the VR invite menu loads every card fully at every character select

**What the player sees.** In VR, each time the character select screen opens, the game stops for about 20 s and memory grows by 11–13 GB (250 girl cards). The second time it doubled (27 GB) and the game ran out of memory and closed.

**Cause.** kPlugVR 3.599 and later build the VR invite list in `MenuInvite.InitFileList()`, called when `VRDealer.SetContext(Context.HSelect)` opens the character select.
It loads every card fully with `kFileParser.GetGirlCardList` / `GetMaleCardList` (including extended data) and keeps them in the static lists `girlInfos` / `boyInfos` (`ChaInfo.file`) until the next character select.
This is the same problem as §9, now in the new VR menu. Normal mode in 3.6.3 still uses `InviteUI.Start`, which the §9 patch handles.

**Suggested fix.** As in §9: keep the path, name, personality and face image in `ChaInfo`, and load the full card only where it is needed:
`MenuInvite.InviteSomeone` (before the card is copied with `CopyAll` and passed to `InviteInVR`) and `MenuInvite.TryToReloadDefAppearence` (which copies `idAndChaFile[id].file`).
The main characters (id 0 / 1) are registered from the list in `InitAndTabAndInfoInH`, so they need the full card too. `InitAndTabAndInfoInH` also changes `parameter.personality` / `voiceRate` of the list entry for darkness, so a reloaded card needs the same values.

**Patch.** `InitFileList()` runs with the light, partial card load of §9 (VR process only). Before `InviteSomeone` and `TryToReloadDefAppearence`, `ChaInfo.file` itself is replaced with the full card, and the darkness values are copied over.
Result in KK VR: about 1.1–1.6 s and +1 GB per character select, and repeated character selects no longer run out of memory.

## 15. Invite menu face textures: full card size, with a CPU copy, and kept after H

**What the player sees.** Nothing visible, but memory: about 250 cards use about 300 MB of GPU memory plus about 300 MB of CPU memory for the face images of the invite menu. In normal mode they stay after H, even on the title screen.

**Cause.**
- Normal mode: `InviteUI.Start()` makes each face image with `PngAssist.ChangeTextureFromByte(pngData, imageW, imageH)` and puts it in `faces` / `dateFaces` / `otherFaces` / `maleFaces`.
  The texture is made with `LoadImage`, so it keeps the card image size (756x1056 for high-resolution cards), has mipmaps and stays readable, which keeps a CPU copy of every image.
  The button only shows `imageW` x `imageH` (a quarter of the screen height x 0.7; 189x270 at 1080p).
  `InviteUI.OnDestroy` does not destroy these textures, and the static `Core.inviteUi` still points to the old `InviteUI` after H, so the lists are not collected until the next H scene replaces it.
- VR (3.599+): `MenuInvite.GetChaInfoList` makes `ChaInfo.face` the same way (card size, mipmaps, readable). In our setup 49 images at 756x1056 took about 198 MB and about 200 at 252x352 about 100 MB (GPU).

**Suggested fix.**
- Make the face texture at the display size (or scale it down once after loading), and call `Apply(true, true)` so the CPU copy is released (`Apply(false, true)` if it is already small enough).
- Destroy the face textures in `InviteUI.OnDestroy`, and clear `Core.inviteUi` when H ends.

**Patch.**
- `ShrinkInviteMenuFaces` (normal mode): after `InviteUI.Start`, images larger than `imageW` x `imageH` are replaced by a scaled-down copy (same aspect ratio, mipmaps, not readable), and the CPU copy of the others is released.
  `ReleaseInviteMenuFaces`: after `InviteUI.OnDestroy`, the four lists are destroyed and cleared.
  Result in KK normal mode (3 H scenes including darkness, 253 cards each): GPU about 299 MB → 64 MB, CPU copy about 299 MB freed, and the textures are destroyed when leaving H. No visible difference.
- `ShrinkVRMenuFaces` (VR): after `MenuInvite.InitFileList`, the same for `ChaInfo.face` with a 252x352 limit (before the menu builds its materials). GPU about 300 MB → 115 MB, no visible difference.

## 16. kPlugVR 3.599+: VR menus are drawn under transparent parts of characters

**What the player sees.** In VR, transparent clothes or hair of characters behind a kPlugVR menu are drawn over the menu. The invite menu also flickers (part of the face list or of the text disappears and comes back), more often while scrolling.

**Cause.** The kPlugVR menus are world-space Canvases (`BaseMenu.frame.menuGo`, `sortingOrder` 0). uGUI does not write depth (`UI/Default` has `ZWrite Off`), so the menu is not hidden by depth; only the draw order decides.
Transparent character parts (`renderQueue` 3000 or more) are drawn after the menu and appear on top of it even when they are behind it.
Parts with the same queue (3000) are sorted by distance, so the order between them and the menu changes from frame to frame, which shows up as flicker.
The patch's diagnostics showed no problem on the CPU side (text meshes, Canvas rendering, overlapping buttons, stencil settings) while the flicker was visible.

**Suggested fix.** Make the menus draw after the characters. Raising the menu Canvas `sortingOrder` works, but then everything else in the transparent queue (for example the pointer ray in front of the menu, or other Canvases with a Mask) is drawn before the menu and can be hidden by it.
Lowering the `sortingOrder` of the characters' transparent renderers instead keeps the order of everything else.

**Patch.** `DrawMenusOnTop` with `Method = CharactersFirst` (default): the transparent renderers of characters get `sortingOrder` -1 (the player's silhouette shader `Custom/Silhouette` is left as is); they are set again after invites and map changes, and restored when the option is turned off.
`Method = MenusLast` raises the menu Canvases to `sortingOrder` 30000 instead (the first attempt; it hid the pointer ray in front of the menu).
Confirmed in KK VR with 9 girls and several map changes: no clothes over the menu, no flicker, and no problems with transparent parts of user maps.

## 17. `KokanBehavior`: the `cf_j_kokan` scale becomes zero, negative or NaN

**What the player sees.** During insertion (seen mostly in the doggy position `khs_f_81`), Unity logs "Infinity or NaN floating point numbers appear when calculating the transform matrix for a Collider" every frame, for `…/cf_j_kokan/Kokan_root/Kokan` (the sphere collider added by `ChaTools.AddColliders`). One VR session had 29,260 of these warnings, most of a 10 MB log.

**Cause.** Every frame, `KokanBehavior` resets the scale of `cf_j_kokan` and, during insertion, adds a squash in `CalculateOverlap` from the movement of the man's hip in that frame (`pistonMag`) divided by `Time.deltaTime`.
When `Time.deltaTime` is 0 the result is infinite or NaN, and when the movement in one frame is very large (the patch saw 11–28 m in one frame) the scale becomes zero or negative.
The value is written to `cf_j_kokan.localScale`, so the child collider gets a broken scale. `OverlapDeformer.ApplyOverlap` uses the same value. The normal-mode kPlug has the same calculation.
In our VR logs the main cause was frames with `Time.deltaTime` = 0 (13,775 and 40,450 calls in 30 s during insertion).

**Suggested fix.** In `CalculateOverlap` / `ApplyOverlap`, skip the squash when `Time.deltaTime` is 0 or less, when `pistonMag` is not finite, or when the movement in one frame is unrealistically large; and clamp the resulting scale to a sane range.

**Patch.** Exactly that: the squash is skipped for those calls (limit 0.15 m per frame), and after `KokanBehavior.LateUpdate` a non-finite or out-of-range scale (0.05–5) of `cf_j_kokan` is repaired.
Result in KK VR: 0 warnings (the same kind of session had 29,260). We have not specifically checked how the doggy position looks with the patch.

## License of this document and the patch

MIT. Feel free to use any part of the patch or of this document in kPlug.
