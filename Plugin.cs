using BepInEx;
using HarmonyLib;
using Receiver2;
using UnityEngine;
using System.Linq;
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using FMODUnity;
using FMOD.Studio;

namespace WinchesterPatch
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private enum ActionState {
            Locked,
            Locking,
            Unlocked,
            Unlocking
        };


        private static readonly int gun_model = 1003;    

        private static GameObject instance;

        private static MethodInfo tryFireBullet;
        private static MethodInfo getLastBullet;
        private static FieldInfo bullet_shake_time;
        private static Type BulletInventory;

        private static LinearMover action_slide = new LinearMover();
        private static ActionState action_state;
        private static TubeMagazineScript magazine;
        private static InventorySlot carrier;
        private static bool decocking;
        private static bool carrierReady = true;

        private static void FireBulletShotgun(GunScript instance, ShellCasingScript round) {
	        instance.chamber_check_performed = false;

	        CartridgeSpec cartridge_spec = default;
	        cartridge_spec.SetFromPreset(round.cartridge_type);
	        LocalAimHandler holdingPlayer = instance.GetHoldingPlayer();

	        Vector3 direction = instance.transform_bullet_fire.rotation * Vector3.forward;
	        BulletTrajectory bulletTrajectory = BulletTrajectoryManager.PlanTrajectory(instance.transform_bullet_fire.position, cartridge_spec, direction, instance.right_hand_twist);
	        if (ConfigFiles.global.display_trajectory_window && ConfigFiles.global.display_trajectory_window_show_debug) {
		        bulletTrajectory.draw_path = BulletTrajectory.DrawType.Debug;
	        } 
            else if (round.tracer || GunScript.force_tracers) {
		        bulletTrajectory.draw_path = BulletTrajectory.DrawType.Tracer;
		        bulletTrajectory.tracer_fuse = true;
	        }

	        if (holdingPlayer != null) {
		        bulletTrajectory.bullet_source = instance.gameObject;
		        bulletTrajectory.bullet_source_entity_type = ReceiverEntityType.Player;
	        } 
            else {
		        bulletTrajectory.bullet_source = instance.gameObject;
		        bulletTrajectory.bullet_source_entity_type = ReceiverEntityType.UnheldGun;
	        }
	        BulletTrajectoryManager.ExecuteTrajectory(bulletTrajectory);

	        instance.rotation_transfer_y += UnityEngine.Random.Range(instance.rotation_transfer_y_min, instance.rotation_transfer_y_max);
	        instance.rotation_transfer_x += UnityEngine.Random.Range(instance.rotation_transfer_x_min, instance.rotation_transfer_x_max);
	        instance.recoil_transfer_x -= UnityEngine.Random.Range(instance.recoil_transfer_x_min, instance.recoil_transfer_x_max);
	        instance.recoil_transfer_y += UnityEngine.Random.Range(instance.recoil_transfer_y_min, instance.recoil_transfer_y_max);
	        instance.add_head_recoil = true;
            
	        if (instance.CanMalfunction && instance.malfunction == GunScript.Malfunction.None && (UnityEngine.Random.Range(0f, 1f) < instance.doubleFeedProbability || instance.force_double_feed_failure))
	        {
		        if (instance.force_double_feed_failure && instance.force_just_one_failure)
		        {
			        instance.force_double_feed_failure = false;
		        }
		        instance.malfunction = GunScript.Malfunction.DoubleFeed;
		        ReceiverEvents.TriggerEvent(ReceiverEventTypeInt.GunMalfunctioned, 2);
	        }
            
	        ReceiverEvents.TriggerEvent(ReceiverEventTypeVoid.PlayerShotFired);

	        instance.last_time_fired = Time.time;
	        instance.last_frame_fired = Time.frameCount;
	        instance.dry_fired = false;

	        if (instance.shots_until_dirty > 0) {
		        instance.shots_until_dirty--;
	        }
        }

        private static System.Collections.IEnumerator moveRoundOnCarrier(GunScript __instance, ShellCasingScript round) {
            carrierReady = false;

            while (round.transform.localPosition.x != 0 || round.transform.localRotation.x != 0) {
                round.transform.localPosition = Vector3.MoveTowards(round.transform.localPosition, Vector3.zero, Time.deltaTime);
                round.transform.localRotation = Quaternion.RotateTowards(round.transform.localRotation, Quaternion.identity, Time.deltaTime * 200);

                yield return null;
            }

            carrierReady = true;

            yield break;
        }

        private static System.Collections.IEnumerator moveRoundToChamber(GunScript __instance, ShellCasingScript round) {
            while (action_slide.amount > 0.90f) {
                yield return null;
            }

            Transform bolt = action_slide.transform.Find("bolt/point_round");

            round.transform.parent = bolt;

            round.transform.rotation = __instance.transform.rotation;

            while (action_slide.amount > 0) {
                round.transform.localRotation = Quaternion.identity;

                round.transform.localPosition = new Vector3(
                    0,
                    Mathf.Lerp(round.transform.localPosition.y, 0, action_slide.amount),
                    0
                );
                
                yield return null;
            }

            yield break;
        }

        [HarmonyPatch(typeof(RuntimeTileLevelGenerator), "PopulateItems")]
        static class PopulateItemsTranspiler {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) {
                CodeMatcher codeMatcher = new CodeMatcher(instructions).MatchForward(false, 
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(GunScript), "gun_type")),
                    new CodeMatch(OpCodes.Ldc_I4_1)
                );

                if (!codeMatcher.ReportFailure(__originalMethod, Debug.LogError)) {
                    codeMatcher.SetOperandAndAdvance(
                        AccessTools.Field(typeof(GunScript), "magazine_root_types")
                    ).InsertAndAdvance(
                        new CodeInstruction(OpCodes.Ldlen)
                    ).SetOpcodeAndAdvance(
                        OpCodes.Ldc_I4_0
                    ).SetOpcodeAndAdvance(
                        OpCodes.Bne_Un_S
                    );
                }

                return codeMatcher.InstructionEnumeration();
            }
        }

        private void Awake()
        {
            // Plugin startup logic
            Logger.LogInfo("Winchester 1897 plugin is loaded!");

            Harmony.CreateAndPatchAll(this.GetType());
            Harmony.CreateAndPatchAll(typeof(PopulateItemsTranspiler));

            tryFireBullet = typeof(GunScript).GetMethod("TryFireBullet", BindingFlags.NonPublic | BindingFlags.Instance);
            getLastBullet = typeof(LocalAimHandler).GetMethod("GetLastMatchingLooseBullet", BindingFlags.NonPublic | BindingFlags.Instance);

            bullet_shake_time = typeof(LocalAimHandler).GetField("show_bullet_shake_time", BindingFlags.NonPublic | BindingFlags.Instance);

            BulletInventory = typeof(LocalAimHandler).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Instance).Single(t => t.Name == "BulletInventory");
        }

        [HarmonyPatch(typeof(ReceiverCoreScript), "Awake")]
        [HarmonyPostfix]
        private static void PatchCoreAwake(ref ReceiverCoreScript __instance, ref GameObject[] ___gun_prefabs_all) {
            GameObject win = ___gun_prefabs_all.Single(go => {
                GunScript gs = go.GetComponent<GunScript>();
                return !(gs == null || (int) gs.gun_model != gun_model);
            });

            __instance.generic_prefabs = new List<InventoryItem>(__instance.generic_prefabs) {
                win.GetComponent<GunScript>(),
                win.GetComponent<GunScript>().loaded_cartridge_prefab.GetComponent<ShellCasingScript>()
            }.ToArray();

            LocaleTactics lt = new LocaleTactics();

            lt.title = "Winchester 1897";
            lt.gun_internal_name = "winchester.winchester_1897";
            lt.text = "A modded pump-action shotgun.\nDeveloped in the late XIX century, Winchester model 97 quickly became popular among hunters, competitive shooters and eventually soldiers, who relied on it's reliability and firepower to survive the trench warfare of WW1";

            Locale.active_locale_tactics.Add("winchester.winchester_1897", lt);

            __instance.PlayerData.unlocked_gun_names.Add("winchester.winchester_1897");
        }

        [HarmonyPatch(typeof(CartridgeSpec), "SetFromPreset")]
        [HarmonyPrefix]
        private static void PatchSetFromPreset(ref CartridgeSpec __instance, CartridgeSpec.Preset preset) {
            if ((int) preset == gun_model) {
                __instance.extra_mass = 25f;
				__instance.mass = 3.5f;
				__instance.speed = 420f;
				__instance.diameter = 0.0084f;
            }
        }

        [HarmonyPatch(typeof(GunScript), "Awake")]
        [HarmonyPostfix]
        public static void PatchGunAwake(ref GunScript __instance, ref float ___hammer_halfcocked, ref float ___hammer_cocked_val) {
            if ((int) __instance.gun_model != gun_model) return;

            instance = __instance.gameObject;

            __instance.hammer.transform = __instance.transform.Find("carrier/hammer");
            __instance.hammer.rotations[0] = __instance.transform.Find("carrier/hammer_fully_in").localRotation;
            __instance.hammer.rotations[1] = __instance.transform.Find("carrier/hammer_fully_out").localRotation;

            __instance.hammer.amount = 0;
            __instance.hammer.UpdateDisplay();

            float fullRot = Quaternion.Angle(__instance.hammer.rotations[0], __instance.hammer.rotations[1]);

            ___hammer_halfcocked = Quaternion.Angle(
                __instance.hammer.rotations[0],
                __instance.transform.Find("carrier/hammer_half_cocked").localRotation
            ) / fullRot;
            
            ___hammer_cocked_val = Quaternion.Angle(
                __instance.hammer.rotations[0],
                __instance.transform.Find("carrier/hammer_cocked").localRotation
            ) / fullRot;

            action_slide.transform = __instance.transform.Find("action_slide");
            action_slide.positions[0] = __instance.transform.Find("action_slide_forward").localPosition;
            action_slide.positions[1] = __instance.transform.Find("action_slide_back").localPosition;

            magazine = __instance.transform.Find("magazine_tube").gameObject.AddComponent<TubeMagazineScript>();

            carrier = __instance.transform.Find("carrier/round_ready").GetComponent<InventorySlot>();
        }

        [HarmonyPatch(typeof(GunScript), "Update")]
        [HarmonyPostfix]
        public static void PatchGunUpdate(ref GunScript __instance, ref int ___hammer_state, ref float ___hammer_halfcocked, ref float ___hammer_cocked_val, ref ExtractorRodStage ___extractor_rod_stage) {
            if ((int) __instance.gun_model != gun_model || Time.timeScale == 0 || !__instance.enabled || __instance.GetHoldingPlayer() == null) return;

            LocalAimHandler lah = LocalAimHandler.player_instance;

            // Decocking logic
            if (lah.character_input.GetButton(14) && lah.character_input.GetButton(2)) decocking = true;

            if (decocking) {
                if (!lah.character_input.GetButton(14)) {
                    __instance.hammer.amount = Mathf.MoveTowards(__instance.hammer.amount, 0, Time.deltaTime * 5);
                }
                if (__instance.hammer.amount == 0 || !lah.character_input.GetButton(2)) {
                    ___hammer_state = 0;
                    decocking = false;
                }
            }

            // Hammer logic
            if (__instance.hammer.amount >= ___hammer_cocked_val ) {
                ___hammer_state = 2;
                if (!lah.character_input.GetButton(14)) {
                    __instance.hammer.amount = ___hammer_cocked_val;
                }
            } else if (__instance.hammer.amount >= ___hammer_halfcocked && __instance.trigger.amount != 1) {
                ___hammer_state = 1;
                if (!lah.character_input.GetButton(14)) {
                    __instance.hammer.amount = Mathf.MoveTowards(__instance.hammer.amount, ___hammer_halfcocked, Time.deltaTime * 25);
                }
            } else if (__instance.trigger.amount != 1){
                ___hammer_state = 0;
                if (!lah.character_input.GetButton(14)) {
                    __instance.hammer.amount = 0;
                }
            }

            // Action open/close logic
            if (lah.character_input.GetButtonDown(10)) {
                if (action_state == ActionState.Locked || action_state == ActionState.Locking) {
                    action_state = ActionState.Unlocking;
                }
                else if ((carrierReady || carrier.contents.Count == 0) && (action_state == ActionState.Unlocked || action_state == ActionState.Unlocking)) {
                    action_state = ActionState.Locking;
                }
                __instance.yoke_stage = YokeStage.Closed;
            }

            // Ammo add toggle logic
            if (lah.character_input.GetButtonDown(11)) {
                if (__instance.yoke_stage == YokeStage.Closed) __instance.yoke_stage = YokeStage.Open;
                else __instance.yoke_stage = YokeStage.Closed;
            }
            if (lah.character_input.GetButtonDown(20)) __instance.yoke_stage = YokeStage.Closed;

            // Ammo insert logic
            if (__instance.yoke_stage == YokeStage.Open && lah.character_input.GetButtonDown(70)) {
                if (action_state == ActionState.Locked && magazine.ammoCount != magazine.maxCapacity && magazine.ready) {
                    var bullet = getLastBullet.Invoke(lah, new object[]
				    {
                        new CartridgeSpec.Preset[] { __instance.loaded_cartridge_prefab.GetComponent<ShellCasingScript>().cartridge_type }
				    });

                    if (bullet != null) {
                        ShellCasingScript round = (ShellCasingScript) BulletInventory.GetField("item", BindingFlags.Public | BindingFlags.Instance).GetValue(bullet);

                        magazine.addRound(round);

                        lah.MoveInventoryItem(round, magazine.slot);
                    }
                }
                else if (action_state == ActionState.Unlocked && carrier.contents.Count == 0) {

                    var bullet = getLastBullet.Invoke(lah, new object[]
				    {
                        new CartridgeSpec.Preset[] { __instance.loaded_cartridge_prefab.GetComponent<ShellCasingScript>().cartridge_type }
				    });

                    if (bullet != null) {
                        ShellCasingScript round = (ShellCasingScript) BulletInventory.GetField("item", BindingFlags.Public | BindingFlags.Instance).GetValue(bullet);

                        lah.MoveInventoryItem(round, carrier);

                        round.transform.parent = __instance.transform.Find("carrier/round_ready");
                        round.transform.localScale = Vector3.one;
                        round.transform.localPosition = Vector3.zero;
                        round.transform.localRotation = Quaternion.identity;

                        AudioManager.PlayOneShotAttached("event:/guns/model10/insert_bullet", __instance.gameObject);
                    }
                }
                else bullet_shake_time.SetValue(lah, Time.time);
            }

            // Fire logic
            if (__instance.trigger.amount == 1 && ___hammer_state == 2 && !lah.character_input.GetButton(14) && action_slide.amount == 0 && !decocking) {
                 __instance.hammer.amount = Mathf.MoveTowards(__instance.hammer.amount, 0, Time.deltaTime * 50);
                 if (__instance.hammer.amount == 0) {
                    Vector3 originalRotation = __instance.transform.Find("point_bullet_fire").localEulerAngles;

                    __instance.transform_bullet_fire.localEulerAngles += new Vector3(
                            UnityEngine.Random.Range(-1, 1),
                            UnityEngine.Random.Range(-1, 1),
                            0
                    );
                    
                    tryFireBullet.Invoke(__instance, new object[] {1});

                    if (!__instance.dry_fired) {
                        AudioManager.PlayOneShotAttached("event:/guns/deagle/shot", __instance.gameObject);

                        for (int i = 0; i < 7; i++) {
                            __instance.transform_bullet_fire.localEulerAngles += new Vector3(
                                UnityEngine.Random.Range(-2f, 2f),
                                UnityEngine.Random.Range(-2f, 2f),
                                0
                            );

                            FireBulletShotgun(__instance, __instance.round_in_chamber);

                            __instance.transform_bullet_fire.localEulerAngles = originalRotation;
                        }
                    }
                    __instance.transform_bullet_fire.localEulerAngles = originalRotation;

                    ___hammer_state = 0;
                 }
            }

            // Hammer cock by slide logic
            if (action_slide.amount > 0) {
                __instance.hammer.amount = Mathf.Max(__instance.hammer.amount, Mathf.Clamp(action_slide.transform.localPosition.z * -40, 0, 1));
            }

            // Action open logic
            if (action_state == ActionState.Unlocking) {
                ___extractor_rod_stage = ExtractorRodStage.Open;
                
                if (action_slide.amount == 1) {
                    action_state = ActionState.Unlocked;

                    ShellCasingScript round;
                    if ((round = magazine.removeRound()) != null && carrier.contents.Count == 0) {
                        round.Move(carrier);

                        round.transform.parent = __instance.transform.Find("carrier/round_ready");

                        __instance.StartCoroutine(moveRoundOnCarrier(__instance, round));
                    }
                    
                    for (int i = 0; i < 5; i++)
                        AudioManager.PlayOneShotAttached("event:/guns/deagle/slide_back_partial", __instance.gameObject);
                }
                else {
                    action_slide.amount = Mathf.MoveTowards(action_slide.amount, 1, Time.deltaTime * 8);
                }

                if (__instance.round_in_chamber != null) {
                    float round_travel = Vector3.Dot(__instance.round_in_chamber.transform.position, __instance.transform.forward);
                    float round_eject = Vector3.Dot(__instance.transform.Find("frame/point_round_eject").position, __instance.transform.forward);

                    if (__instance.round_in_chamber != null && round_travel < round_eject) {
                        __instance.EjectRoundInChamber(0.45f);
                    }
                }
            }

            // Action close logic
            if (action_state == ActionState.Locking) {
                ___extractor_rod_stage = ExtractorRodStage.Closed;

                if (action_slide.amount == 0) {
                    action_state = ActionState.Locked;

                    if (__instance.round_in_chamber != null) __instance.round_in_chamber.transform.parent = action_slide.transform.Find("bolt");

                    for (int i = 0; i < 5; i++)
                        AudioManager.PlayOneShotAttached("event:/guns/deagle/slide_back_partial", __instance.gameObject);
                }
                else {
                    action_slide.amount = Mathf.MoveTowards(action_slide.amount, 0, Time.deltaTime * 8);
                }

                if (carrier.contents.Count > 0) {
                    ShellCasingScript round = (ShellCasingScript) carrier.contents.ElementAt(0);

                    __instance.StartCoroutine(moveRoundToChamber(__instance, round));

                    __instance.ReceiveRound(round);

                    round.transform.parent = carrier.transform;
                }
            }

            // Gun Animations
            __instance.ApplyTransform("carrier_move", action_slide.amount, __instance.transform.Find("carrier"));
            __instance.ApplyTransform("bolt_move", action_slide.amount, __instance.transform.Find("action_slide/bolt"));

            //Movers update
            action_slide.UpdateDisplay();
            __instance.hammer.UpdateDisplay();
        }
    }
}
