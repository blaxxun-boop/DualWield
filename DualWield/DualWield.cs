using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using SkillManager;
using UnityEngine;
#if DPSLOG
using Debug = UnityEngine.Debug;
using System.Diagnostics;
#endif

namespace DualWield;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
public class DualWield : BaseUnityPlugin
{
#if DPSLOG
	private static readonly Stopwatch stopwatch = new();
	private static readonly List<float> receivedDmg = new();
#endif

	private const string ModName = "Dual Wield";
	private const string ModVersion = "1.0.6";
	private const string ModGUID = "org.bepinex.plugins.dualwield";

	private static readonly ConfigSync configSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion };

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private enum Toggle
	{
		On = 1,
		Off = 0
	}

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	private static ConfigEntry<string> dualWieldExclusionList = null!;
	private static ConfigEntry<float> experienceGainFactor = null!;
	private static ConfigEntry<int> experienceLoss = null!;
	private static ConfigEntry<Toggle> singleOffhandSkill = null!;

	private static AssetBundle asset = null!;
	private static readonly Dictionary<string, Dictionary<string, string>> replacementMap = new();
	private static readonly Dictionary<string, int> attackMap = new();
	private static readonly Dictionary<string, AnimationClip> ExternalAnimations = new();
	private static readonly Dictionary<string, RuntimeAnimatorController> CustomRuntimeControllers = new();
	private static readonly List<string> DualWieldExclusion = new();

	private static readonly Dictionary<Skills.SkillType, Skills.SkillType> skillMap = new();

	private static readonly Dictionary<Skills.SkillType, AnimationBalancing[]> balancingMapDefault = new()
	{
		{
			Skills.SkillType.Axes, new[]
			{
				new AnimationBalancing { speed = 0.6f, damage = 0.9f, stamina = 40 },
				new AnimationBalancing { speed = 0.8f, damage = 0.9f, stamina = 20 },
				new AnimationBalancing { speed = 0.7f, damage = 0.95f, stamina = 20 },
				new AnimationBalancing { speed = 0.6f, damage = 1.7f, stamina = 30 }
			}
		},
		{
			Skills.SkillType.Clubs, new[]
			{
				new AnimationBalancing { speed = 0.8f, damage = 0.6f, stamina = 35 },
				new AnimationBalancing { speed = 1.2f, damage = 0.65f, stamina = 14 },
				new AnimationBalancing { speed = 1.3f, damage = 0.75f, stamina = 15 },
				new AnimationBalancing { speed = 1.5f, damage = 0.75f, stamina = 15 }
			}
		},
		{
			Skills.SkillType.Knives, new[]
			{
				new AnimationBalancing { speed = 1.4f, damage = 0.3f, stamina = 32 },
				new AnimationBalancing { speed = 1.4f, damage = 0.2f, stamina = 9 },
				new AnimationBalancing { speed = 1.4f, damage = 0.2f, stamina = 9 },
				new AnimationBalancing { speed = 2f, damage = 1.4f, stamina = 27 }
			}
		},
		{
			Skills.SkillType.Swords, new[]
			{
				new AnimationBalancing { speed = 1, damage = 0.5f, stamina = 40 },
				new AnimationBalancing { speed = 1, damage = 0.9f, stamina = 15 },
				new AnimationBalancing { speed = 1, damage = 0.9f, stamina = 15 },
				new AnimationBalancing { speed = 1.2f, damage = 0.9f, stamina = 20 }
			}
		}
	};

	private struct AnimationBalancing
	{
		public float speed;
		public float damage;
		public float stamina;
	}

	private static readonly Dictionary<Skills.SkillType, AnimationBalancingConfig[]> balancingMap = new();

	private struct AnimationBalancingConfig
	{
		public ConfigEntry<float> speed;
		public ConfigEntry<float> damage;
		public ConfigEntry<float> stamina;
	}

	private static void InitSwordAnimation()
	{
		replacementMap["DWswords"] = new Dictionary<string, string>
		{
			["fight idle"] = "BlockExternal",
			["Block idle"] = "BlockExternal",
		};
		foreach (KeyValuePair<string, int> kv in attackMap)
		{
			replacementMap["DWswords"][kv.Key] = kv.Value == 0 ? "DualSwordsSpecial" : $"Attack{kv.Value}External";
		}
	}

	private static void InitAxesAnimation()
	{
		replacementMap["DWaxes"] = new Dictionary<string, string>
		{
			["fight idle"] = "BlockExternal",
			["Block idle"] = "BlockExternal"
		};
		foreach (KeyValuePair<string, int> kv in attackMap)
		{
			replacementMap["DWaxes"][kv.Key] = kv.Value == 0 ? "DualAxesSpecial" : $"Attack{kv.Value}External";
		}
	}

	public void Awake()
	{
		List<Skill> skills = new();

		Skill skill = new("Dual Axes", "dualaxes.png");
		skill.Description.English("Increases the damage done with your left hand, when dual wielding axes.");
		skill.Configurable = false;
		skills.Add(skill);

		skill = new Skill("Dual Clubs", "dualclubs.png");
		skill.Description.English("Increases the damage done with your left hand, when dual wielding clubs.");
		skill.Configurable = false;
		skills.Add(skill);

		skill = new Skill("Dual Knives", "dualknives.png");
		skill.Description.English("Increases the damage done with your left hand, when dual wielding knives.");
		skill.Configurable = false;
		skills.Add(skill);

		skill = new Skill("Dual Swords", "dualswords.png");
		skill.Description.English("Increases the damage done with your left hand, when dual wielding swords.");
		skill.Configurable = false;
		skills.Add(skill);

		skill = new Skill("Dual Offhand", "dualswords.png");
		skill.Description.English("Increases the damage done with your left hand, when dual wielding.");
		skill.Configurable = false;
		skills.Add(skill);

		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		dualWieldExclusionList = config("1 - General", "Dual Wield Exclusion", "", "List prefab names of weapons that should be excluded from being dual-wielded. Comma separated.");
		dualWieldExclusionList.SettingChanged += UpdateExclusionList;
		experienceGainFactor = config("1 - General", "Dual Wield Experience Factor", 1f, new ConfigDescription("Factor for experience gained for the dual wielding skills.", new AcceptableValueRange<float>(0f, 5f)));
		experienceGainFactor.SettingChanged += (_, _) =>
		{
			foreach (Skill dualSkill in skills)
			{
				dualSkill.SkillGainFactor = experienceGainFactor.Value;
			}
		};
		foreach (Skill dualSkill in skills)
		{
			dualSkill.SkillGainFactor = experienceGainFactor.Value;
		}
		experienceLoss = config("1 - General", "Skill Experience Loss", 5, new ConfigDescription("How much experience to lose in the dual wielding skills on death.", new AcceptableValueRange<int>(0, 100)));
		experienceLoss.SettingChanged += (_, _) =>
		{
			foreach (Skill dualSkill in skills)
			{
				dualSkill.SkillLoss = experienceLoss.Value;
			}
		};
		foreach (Skill dualSkill in skills)
		{
			dualSkill.SkillLoss = experienceLoss.Value;
		}
		singleOffhandSkill = config("1 - General", "Single Offhand Skill", Toggle.Off, new ConfigDescription("If on, all weapon types share a single offhand skill."));
		singleOffhandSkill.SettingChanged += (_, _) => ToggleOffhandSkill();

		ToggleOffhandSkill();

		asset = GetAssetBundle("dwanimations");
		ExternalAnimations["Attack1External"] = asset.LoadAsset<AnimationClip>("Attack1");
		ExternalAnimations["Attack2External"] = asset.LoadAsset<AnimationClip>("Attack2");
		ExternalAnimations["Attack3External"] = asset.LoadAsset<AnimationClip>("Attack3");
		ExternalAnimations["BlockExternal"] = asset.LoadAsset<AnimationClip>("DWblock");
		ExternalAnimations["DualSwordsSpecial"] = asset.LoadAsset<AnimationClip>("DWspecial");
		ExternalAnimations["DualAxesSpecial"] = asset.LoadAsset<AnimationClip>("DWspecial2");
		attackMap["Sword-Attack-R4"] = 0;
		attackMap["Knife JumpAttack"] = 0;
		attackMap["MaceAltAttack"] = 0;
		attackMap["Attack1"] = 1;
		attackMap["Attack2"] = 2;
		attackMap["Attack3"] = 3;
		attackMap["Axe Secondary Attack"] = 0;
		attackMap["axe_swing"] = 1;
		attackMap["Axe combo 2"] = 2;
		attackMap["Axe combo 3"] = 3;
		attackMap["knife_slash0"] = 1;
		attackMap["knife_slash1"] = 2;
		attackMap["knife_slash2"] = 3;

		InitSwordAnimation();
		InitAxesAnimation();

		foreach (KeyValuePair<Skills.SkillType, AnimationBalancing[]> balancingKv in balancingMapDefault)
		{
			AnimationBalancingConfig[] balancingConfigs = new AnimationBalancingConfig[4];
			for (int i = 0; i < balancingKv.Value.Length; ++i)
			{
				AnimationBalancing balancing = balancingKv.Value[i];
				string configName = i == 0 ? "Special attack" : $"Attack {i}";
				balancingConfigs[i] = new AnimationBalancingConfig
				{
					damage = config("2 - " + balancingKv.Key, $"{configName} - Damage", balancing.damage * 100, new ConfigDescription(i == 0 ? $"The damage dealt by the special attack with {balancingKv.Key} as a percentage of the weapon damage." : $"The damage dealt by the {i}. attack of the {balancingKv.Key} attack combo as a percentage of the weapon damage.", new AcceptableValueRange<float>(0, 500))),
					speed = config("2 - " + balancingKv.Key, $"{configName} - Speed", balancing.speed * 100, new ConfigDescription(i == 0 ? $"The attack speed of the special attack with {balancingKv.Key} as a percentage." : $"The attack speed of the {i}. attack of the {balancingKv.Key} attack combo. Provided as a percentage.", new AcceptableValueRange<float>(1, 300))),
					stamina = config("2 - " + balancingKv.Key, $"{configName} - Stamina", balancing.stamina, new ConfigDescription(i == 0 ? $"The stamina usage of the special attack with {balancingKv.Key}." : $"The stamina usage of the {i}. attack of the {balancingKv.Key} attack combo.", new AcceptableValueRange<float>(0, 200)))
				};
			}
			balancingMap[balancingKv.Key] = balancingConfigs;
		}

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);

		UpdateExclusionList(null, null);
	}

	private static void ToggleOffhandSkill()
	{
		skillMap.Clear();

		if (singleOffhandSkill.Value == Toggle.On)
		{
			skillMap.Add(Skills.SkillType.Swords, Skill.fromName("Dual Offhand"));
			skillMap.Add(Skills.SkillType.Knives, Skill.fromName("Dual Offhand"));
			skillMap.Add(Skills.SkillType.Clubs, Skill.fromName("Dual Offhand"));
			skillMap.Add(Skills.SkillType.Axes, Skill.fromName("Dual Offhand"));
		}
		else
		{
			skillMap.Add(Skills.SkillType.Axes, Skill.fromName("Dual Axes"));
			skillMap.Add(Skills.SkillType.Clubs, Skill.fromName("Dual Clubs"));
			skillMap.Add(Skills.SkillType.Knives, Skill.fromName("Dual Knives"));
			skillMap.Add(Skills.SkillType.Swords, Skill.fromName("Dual Swords"));
		}
	}

	private static void UpdateExclusionList(object? sender, EventArgs? e)
	{
		DualWieldExclusion.Clear();
		foreach (string s in dualWieldExclusionList.Value.Split(','))
		{
			DualWieldExclusion.Add(s.Trim());
		}
	}

	private static AssetBundle GetAssetBundle(string filename)
	{
		Assembly assembly = Assembly.GetExecutingAssembly();

		string resourceName = assembly.GetManifestResourceNames().Single(str => str.EndsWith(filename));

		using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
		return AssetBundle.LoadFromStream(stream);
	}

	private static RuntimeAnimatorController MakeAOC(Dictionary<string, string> replacement, RuntimeAnimatorController ORIGINAL)
	{
		AnimatorOverrideController aoc = new(ORIGINAL);
		List<KeyValuePair<AnimationClip, AnimationClip>> anims = new();
		foreach (AnimationClip animation in aoc.animationClips)
		{
			string name = animation.name;
			if (replacement.ContainsKey(name))
			{
				AnimationClip newClip = Instantiate(ExternalAnimations[replacement[name]]);
				newClip.name = name;
				anims.Add(new KeyValuePair<AnimationClip, AnimationClip>(animation, newClip));
			}
			else
			{
				anims.Add(new KeyValuePair<AnimationClip, AnimationClip>(animation, animation));
			}
		}
		aoc.ApplyOverrides(anims);
		return aoc;
	}

	private static void FastReplaceRAC(Player player, RuntimeAnimatorController replace)
	{
		if (player.m_animator.runtimeAnimatorController == replace)
		{
			return;
		}

		player.m_animator.runtimeAnimatorController = replace;
		player.m_animator.Update(Time.deltaTime);
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Start))]
	private static class Patch_Player_Start
	{
		private static void Postfix(Player __instance)
		{
			if (CustomRuntimeControllers.Count == 0 && Player.m_localPlayer is not null)
			{
				CustomRuntimeControllers["Original"] = MakeAOC(new Dictionary<string, string>(), __instance.m_animator.runtimeAnimatorController);
				CustomRuntimeControllers["DWswords"] = MakeAOC(replacementMap["DWswords"], __instance.m_animator.runtimeAnimatorController);
				CustomRuntimeControllers["DWaxes"] = MakeAOC(replacementMap["DWaxes"], __instance.m_animator.runtimeAnimatorController);
			}
		}
	}

	[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
	private static class Patch_Humanoid_EquipItem
	{
		private static bool CheckDualOneHandedWeaponEquip(Humanoid __instance, ItemDrop.ItemData item, bool triggerEquipEffects)
		{
			if (__instance is Player player && player.m_rightItem?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon && player.m_rightItem.m_shared.m_skillType == item.m_shared.m_skillType && item.m_shared.m_skillType is not Skills.SkillType.Spears && !DualWieldExclusion.Contains(item.m_dropPrefab.name))
			{
				if (player.m_leftItem != null)
				{
					player.UnequipItem(player.m_leftItem, triggerEquipEffects);
				}
				player.m_leftItem = item;
				return true;
			}
			return false;
		}

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> instrs = instructions.ToList();
			for (int i = 0; i < instrs.Count; ++i)
			{
				yield return instrs[i];
				if (i >= 4 && instrs[i - 4].opcode == OpCodes.Ldarg_1 && instrs[i - 1].opcode == OpCodes.Ldc_I4_3 && instrs[i].opcode == OpCodes.Bne_Un)
				{
					// passed item is compared to ItemDrop.ItemData.ItemType.OneHandedWeapon (value = 3)
					yield return new CodeInstruction(OpCodes.Ldarg_0);
					yield return new CodeInstruction(OpCodes.Ldarg_1);
					yield return new CodeInstruction(OpCodes.Ldarg_2);
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Patch_Humanoid_EquipItem), nameof(CheckDualOneHandedWeaponEquip)));
					yield return new CodeInstruction(OpCodes.Brtrue, instrs[i].operand);
				}
			}
		}

		private static void Prefix()
		{
			Patch_Humanoid_SetupEquipment.ManipulatingEquipment = true;
		}

		private static void Postfix()
		{
			Patch_Humanoid_SetupEquipment.ManipulatingEquipment = false;
		}
	}

	[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipAllItems))]
	private static class Patch_Humanoid_UnequipAllItems
	{
		private static void Prefix()
		{
			Patch_Humanoid_SetupEquipment.ManipulatingEquipment = true;
		}

		private static void Postfix()
		{
			Patch_Humanoid_SetupEquipment.ManipulatingEquipment = false;
		}
	}

	[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.SetupEquipment))]
	private static class Patch_Humanoid_SetupEquipment
	{
		public static bool ManipulatingEquipment = false;

		private static void Prefix(Humanoid __instance)
		{
			if (__instance is Player player && !ManipulatingEquipment && player.m_rightItem == null && player.m_leftItem?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon)
			{
				ItemDrop.ItemData leftItem = player.m_leftItem;
				player.UnequipItem(player.m_leftItem, false);
				player.m_rightItem = leftItem;
				player.m_rightItem.m_equiped = true;
				player.m_leftItem = null;
			}
		}
	}

	[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.ShowHandItems))]
	private static class SwapHiddenItemsEquipOrder
	{
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			FieldInfo leftHand = AccessTools.DeclaredField(typeof(Humanoid), nameof(Humanoid.m_hiddenLeftItem));
			FieldInfo rightHand = AccessTools.DeclaredField(typeof(Humanoid), nameof(Humanoid.m_hiddenRightItem));
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.LoadsField(leftHand))
				{
					yield return new CodeInstruction(OpCodes.Ldfld, rightHand);
				}
				else if (instruction.LoadsField(rightHand))
				{
					yield return new CodeInstruction(OpCodes.Ldfld, leftHand);
				}
				else
				{
					yield return instruction;
				}
			}
		}
	}

	[HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.SetWeaponTrails))]
	private static class Patch_VisEquipment_SetWeaponTrails
	{
		private static void Postfix(VisEquipment __instance, bool enabled)
		{
			if (__instance.m_leftItemInstance)
			{
				foreach (MeleeWeaponTrail trail in __instance.m_leftItemInstance.GetComponentsInChildren<MeleeWeaponTrail>())
				{
					trail.Emit = enabled;
				}
			}
		}
	}

	[HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.AttachItem))]
	private static class Patch_VisEquipment_AttachBackItem
	{
		private static void Postfix(VisEquipment __instance, Transform joint)
		{
			if (!__instance.m_isPlayer)
			{
				return;
			}

			if (joint == __instance.m_backMelee)
			{
				if (joint.childCount > 1)
				{
					joint.GetChild(1).localPosition = new Vector3(-0.003f, 0, 0.003f);
					joint.GetChild(1).localEulerAngles = new Vector3(0f, 80f, 0f);
				}
			}

			if (joint == __instance.m_backTool)
			{
				if (joint.childCount > 1)
				{
					joint.GetChild(1).localPosition = new Vector3(-0.0008f, -0.0052f, 0f);
					joint.GetChild(1).localEulerAngles = new Vector3(20f, 0f, 0f);
				}
			}
		}
	}

	[HarmonyPatch(typeof(ZSyncAnimation), nameof(ZSyncAnimation.RPC_SetTrigger))]
	private static class Patch_ZSyncAnimation_RPC_SetTrigger
	{
		private static void Prefix(ZSyncAnimation __instance, string name)
		{
			if (__instance.GetComponent<Player>() is { } player)
			{
				// ReSharper disable once PatternAlwaysMatches
				ItemDrop.ItemData.SharedData? sharedData(Func<VisEquipment, int> eq) => eq(player.m_visEquipment) is int hash and not 0 ? ObjectDB.instance.GetItemPrefab(hash)?.GetComponent<ItemDrop>()?.m_itemData.m_shared : null;
				ItemDrop.ItemData.SharedData? rightHand = sharedData(v => v.m_currentRightItemHash);
				ItemDrop.ItemData.SharedData? leftHand = sharedData(v => v.m_currentLeftItemHash);
				bool HasAttackName(string? anim) => (anim != null && name.StartsWith(anim, StringComparison.Ordinal) && anim.Length <= name.Length + 1) || anim == "swing_axe";
				bool HasAttack(ItemDrop.ItemData.SharedData? data) => HasAttackName(data?.m_attack.m_attackAnimation) || HasAttackName(data?.m_secondaryAttack.m_attackAnimation);
				if (HasAttack(leftHand) || HasAttack(rightHand))
				{
					bool Onehanded(ItemDrop.ItemData.SharedData? data) => data?.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon;
					string controllerName = "Original";
					if (Onehanded(leftHand) && Onehanded(rightHand))
					{
						controllerName = "DWswords";
						if (name == "mace_secondary" || rightHand!.m_attack.m_attackAnimation == "swing_axe" || rightHand.m_secondaryAttack.m_attackAnimation == "axe_secondary")
						{
							controllerName = "DWaxes";
						}
					}
					// in case this is called before the first Player.Start
					if (CustomRuntimeControllers.TryGetValue(controllerName, out RuntimeAnimatorController controller))
					{
						FastReplaceRAC(player, controller);
					}
				}
			}
		}
	}

	[HarmonyPatch(typeof(Attack), nameof(Attack.OnAttackTrigger))]
	private static class Patch_Attack_OnAttackTrigger
	{
		private class DmgFactor
		{
			public float dmgFactor;
			public float knockback;
		}

		private static Attack.HitPointType? originalHitpointType;
		private static readonly Dictionary<ItemDrop.ItemData.SharedData, DmgFactor> alteredSharedData = new();

		private static void ApplyDmgFactor(bool reverse, string attackAnimation)
		{
			foreach (KeyValuePair<ItemDrop.ItemData.SharedData, DmgFactor> kv in alteredSharedData)
			{
				ItemDrop.ItemData.SharedData item = kv.Key;
				if (item.m_skillType is Skills.SkillType.Clubs or Skills.SkillType.Axes && attackAnimation is "axe_secondary" or "mace_secondary")
				{
					if (reverse)
					{
						item.m_attackForce = kv.Value.knockback;
					}
					else
					{
						kv.Value.knockback = item.m_attackForce;
						item.m_attackForce = 0;
					}
				}
				float dmgFactor = reverse ? 1 / kv.Value.dmgFactor : kv.Value.dmgFactor;
				item.m_damages.Modify(dmgFactor);
				item.m_backstabBonus /= dmgFactor;
			}
		}

		private static void Prefix(Attack __instance)
		{
			if (__instance.m_character is Player && __instance.m_character.m_leftItem?.m_shared is { } leftHand && __instance.m_character.m_rightItem?.m_shared is { } rightHand && leftHand.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon && rightHand.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon)
			{
				if (attackMap.TryGetValue(__instance.m_character.m_animator.GetCurrentAnimatorClipInfo(0)[0].clip.name, out int attackId) && balancingMap.ContainsKey(rightHand.m_skillType))
				{
					float dmgFactor = balancingMap[rightHand.m_skillType][attackId].damage.Value / 100;
					alteredSharedData.Add(rightHand, new DmgFactor { dmgFactor = dmgFactor });
					if (rightHand != leftHand)
					{
						alteredSharedData.Add(leftHand, new DmgFactor { dmgFactor = dmgFactor });
					}
					ApplyDmgFactor(false, __instance.m_attackAnimation);
				}

				originalHitpointType = __instance.m_hitPointtype;
				__instance.m_hitPointtype = Attack.HitPointType.First;
				__instance.m_attackAngle = -__instance.m_attackAngle;
				__instance.m_weapon = __instance.m_character.m_leftItem;
				Skills.SkillType originalType = __instance.m_character.m_leftItem.m_shared.m_skillType;
				if (skillMap.ContainsKey(originalType))
				{
					__instance.m_character.m_leftItem.m_shared.m_skillType = skillMap[originalType];
				}

				__instance.DoMeleeAttack();

				__instance.m_character.m_leftItem.m_shared.m_skillType = originalType;
				__instance.m_weapon = __instance.m_character.m_rightItem;
				__instance.m_attackAngle = -__instance.m_attackAngle;
			}
		}

		private static void Postfix(Attack __instance)
		{
			if (originalHitpointType != null)
			{
				__instance.m_hitPointtype = (Attack.HitPointType)originalHitpointType;
				originalHitpointType = null;
			}

			ApplyDmgFactor(true, __instance.m_attackAnimation);
			alteredSharedData.Clear();
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.GetRandomSkillFactor))]
	private static class Patch_Skills_GetRandomSkillFactor
	{
		private static bool Prefix(Player __instance, Skills.SkillType skill, ref float __result)
		{
			if (skillMap.ContainsValue(skill))
			{
				__result = 0.03f + __instance.GetSkillFactor(skill) * 1.5f;
				return false;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(Attack), nameof(Attack.GetAttackStamina))]
	private static class Patch_Attack_GetAttackStamina
	{
		private static bool Prefix(Attack __instance, ref float __result)
		{
			bool Onehanded(ItemDrop.ItemData? item) => item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon;
			bool dualWield = Onehanded(__instance.m_character.m_rightItem) && Onehanded(__instance.m_character.m_leftItem);

			if (__instance.m_character is Player && dualWield && balancingMap.ContainsKey(__instance.m_weapon.m_shared.m_skillType))
			{
				float attackStamina = balancingMap[__instance.m_weapon.m_shared.m_skillType][__instance.m_attackChainLevels <= 1 ? 0 : __instance.m_currentAttackCainLevel + 1].stamina.Value;
				__result = attackStamina - attackStamina / 6 * __instance.m_character.GetSkillFactor(__instance.m_weapon.m_shared.m_skillType) - attackStamina / 6 * __instance.m_character.GetSkillFactor(skillMap[__instance.m_weapon.m_shared.m_skillType]);

				return false;
			}

			return true;
		}
	}

	[HarmonyPatch(typeof(Attack), nameof(Attack.DoMeleeAttack))]
	private static class Patch_Attack_DoMeleeAttack
	{
		// hit the target from the other side (first for loop), from left to right instead of from right to left: when the angle is negative, start on left side
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator gen)
		{
			MethodInfo quaternionCreator = AccessTools.DeclaredMethod(typeof(Quaternion), nameof(Quaternion.Euler), new[] { typeof(float), typeof(float), typeof(float) });
			FieldInfo attackAngle = AccessTools.DeclaredField(typeof(Attack), nameof(Attack.m_attackAngle));
			List<CodeInstruction> instrs = instructions.ToList();
			for (int i = 0; i < instrs.Count; ++i)
			{
				if (instrs[i].opcode == OpCodes.Neg && instrs[i + 2].opcode == OpCodes.Call && instrs[i + 2].OperandIs(quaternionCreator))
				{
					Label label = gen.DefineLabel();
					yield return new CodeInstruction(OpCodes.Ldarg_0);
					yield return new CodeInstruction(OpCodes.Ldfld, attackAngle);
					yield return new CodeInstruction(OpCodes.Ldc_R4, 0f);
					yield return new CodeInstruction(OpCodes.Ble, label);
					yield return new CodeInstruction(OpCodes.Neg);
					instrs[i + 1].labels.Add(label);
				}
				else
				{
					yield return instrs[i];
				}
				if (instrs[i].opcode == OpCodes.Ldfld && instrs[i].OperandIs(attackAngle))
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Mathf), nameof(Mathf.Abs), new[] { typeof(float) }));
				}
			}
		}
	}

#if DPSLOG
	[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.OnDamaged))]
	private static class dmg
	{
		private static void Postfix(HitData hit)
		{
			if (hit.m_attacker == Player.m_localPlayer.GetZDOID())
			{
				receivedDmg.Add(hit.GetTotalDamage());
			}
		}
	}

	[HarmonyPatch(typeof(Attack), nameof(Attack.OnAttackDone))]
	public static class Patch_atkdone
	{
		private static void Prefix(Attack __instance)
		{
			if (__instance.m_character is Player && (__instance.m_currentAttackCainLevel == __instance.m_attackChainLevels - 1 || __instance.m_attackChainLevels == 0))
			{
				bool dual = __instance.m_character.m_leftItem?.m_shared is { } leftHand && __instance.m_character.m_rightItem?.m_shared is { } rightHand && leftHand.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon && rightHand.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon;
				stopwatch.Stop();
				Debug.Log((!dual ? "Single wield: " : "Dual wield: ") + $" {string.Join(", ", receivedDmg)} dmg; total damage: {receivedDmg.Sum()} in {stopwatch.ElapsedMilliseconds} ms are {receivedDmg.Sum() / (stopwatch.ElapsedMilliseconds / 1000f)} dps");
			}
		}
	}

	[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
	public static class Patch_Humanoid_StartAttack
	{
		private static void Postfix(Humanoid __instance, Attack? __state, bool __result)
		{
			Attack? w = __instance.m_currentAttack;
			if (__result && __instance is Player && w?.m_currentAttackCainLevel == 0)
			{
				stopwatch.Restart();
				receivedDmg.Clear();
			}
		}
	}
#endif

	[HarmonyPatch(typeof(CharacterAnimEvent), nameof(CharacterAnimEvent.FixedUpdate))]
	public static class Patch_CharacterAnimEvent_FixedUpdate
	{
		[UsedImplicitly]
		[HarmonyPriority(Priority.LowerThanNormal)]
		private static void Prefix(Character ___m_character, ref Animator ___m_animator)
		{
			if (!___m_character.IsPlayer() || !___m_character.InAttack())
			{
				return;
			}

			//Debug.Log(___m_animator.GetCurrentAnimatorClipInfo(0)[0].clip.name);

			// check if our marker bit is present and not within float epsilon
			if (___m_animator.speed * 1e4f % 10 is >= 1 and <= 3 || ___m_animator.speed <= 0.001f)
			{
				return;
			}

			Player player = (Player)___m_character;
			Attack? currentAttack = player.m_currentAttack;
			if (currentAttack == null)
			{
				return;
			}

			AnimatorClipInfo[] animInfo = ___m_character.m_animator.GetCurrentAnimatorClipInfo(0);
			if (animInfo.Length == 0)
			{
				return;
			}

			if (!attackMap.TryGetValue(animInfo[0].clip.name, out int attackId))
			{
				return;
			}

			float speedFactor = 1f;

			ItemDrop.ItemData.SharedData? sharedData(int hash) => hash == 0 ? null : ObjectDB.instance.GetItemPrefab(hash)?.GetComponent<ItemDrop>()?.m_itemData.m_shared;
			if (sharedData(player.m_visEquipment.m_currentLeftItemHash) is { } leftHand && sharedData(player.m_visEquipment.m_currentRightItemHash) is { } rightHand && leftHand.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon && rightHand.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon && balancingMap.ContainsKey(rightHand.m_skillType))
			{
				speedFactor = balancingMap[rightHand.m_skillType][attackId].speed.Value / 100;
			}

			___m_animator.speed = (float)Math.Round(___m_animator.speed * speedFactor, 3) + ___m_animator.speed % 1e-4f + 2e-4f;
		}
	}
}
