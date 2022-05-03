using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DualWield;

public static class DualWieldSkills
{
	private class SkillDetails
	{
		public string skillName = null!;
		public string internalSkillName = null!;
		public Skills.SkillDef skillDef = null!;
	}

	private static readonly Dictionary<int, SkillDetails> skills = new();

	public static void AddNewSkill<T>(T skill, string skillName, string skillDescription, float skillIncreaseStep, Sprite skillIcon) where T : struct, IConvertible
	{
		skills[skill.ToInt32(CultureInfo.InvariantCulture)] = new SkillDetails
		{
			skillDef = new Skills.SkillDef
			{
				m_description = skillDescription,
				m_icon = skillIcon,
				m_increseStep = skillIncreaseStep,
				m_skill = (Skills.SkillType)(object)skill
			},
			internalSkillName = skill.ToString(),
			skillName = skillName
		};
	}

	[HarmonyPatch(typeof(Skills), nameof(Skills.GetSkillDef))]
	private static class Patch_Skills_GetSkillDef
	{
		private static void Postfix(ref Skills.SkillDef? __result, List<Skills.SkillDef> ___m_skills, Skills.SkillType type)
		{
			if (__result is null && GetSkillDef(type) is { } skillDef)
			{
				___m_skills.Add(skillDef);
				__result = skillDef;
			}
		}
	}

	[HarmonyPatch(typeof(Skills), nameof(Skills.CheatRaiseSkill))]
	private static class Patch_Skills_CheatRaiseskill
	{
		[HarmonyPrefix]
		private static bool Prefix(Skills __instance, string name, float value, Player ___m_player)
		{
			foreach (int id in skills.Keys)
			{
				SkillDetails skillDetails = skills[id];

				if (string.Equals(skillDetails.internalSkillName, name, StringComparison.CurrentCultureIgnoreCase))
				{
					Skills.Skill skill = __instance.GetSkill((Skills.SkillType)id);
					skill.m_level += value;
					skill.m_level = Mathf.Clamp(skill.m_level, 0f, 100f);
					___m_player.Message(MessageHud.MessageType.TopLeft, "Skill increased " + skillDetails.skillName + ": " + (int)skill.m_level, 0, skill.m_info.m_icon);
					Console.instance.Print("Skill " + skillDetails.skillName + " = " + skill.m_level);
					return false;
				}
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(Skills), nameof(Skills.CheatResetSkill))]
	private static class Patch_Skills_CheatResetSkill
	{
		[HarmonyPrefix]
		private static bool Prefix(Skills __instance, string name)
		{
			foreach (int id in skills.Keys)
			{
				SkillDetails skillDetails = skills[id];

				if (string.Equals(skillDetails.internalSkillName, name, StringComparison.CurrentCultureIgnoreCase))
				{
					__instance.ResetSkill((Skills.SkillType)id);
					Console.instance.Print("Skill " + skillDetails.skillName + " reset");
					return false;
				}
			}
			return true;
		}
	}

	private static Skills.SkillDef? GetSkillDef(Skills.SkillType skillType)
	{
		int id = (int)skillType;

		if (!skills.ContainsKey(id))
		{
			return null;
		}

		SkillDetails skillDetails = skills[id];

		Localization.instance.AddWord("skill_" + id, skillDetails.skillName);

		return skillDetails.skillDef;
	}

	[HarmonyPatch(typeof(Skills), nameof(Skills.IsSkillValid))]
	private static class Patch_Skills_IsSkillValid
	{
		private static void Postfix(Skills.SkillType type, ref bool __result)
		{
			if (__result)
			{
				return;
			}

			if (skills.ContainsKey((int)type))
			{
				__result = true;
			}
		}
	}
	
	[HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
	private static class Patch_Terminal_InitTerminal
	{
		private static bool InitializedTerminal = false;

		private static void Prefix() => InitializedTerminal = Terminal.m_terminalInitialized;
		
		private static void Postfix()
		{
			if (InitializedTerminal)
			{
				return;
			}

			void AddSkill(Terminal.ConsoleCommand command)
			{
				Terminal.ConsoleOptionsFetcher fetcher = command.m_tabOptionsFetcher;
				command.m_tabOptionsFetcher = () =>
				{
					List<string> options = fetcher();
					options.AddRange(skills.Values.Select(skill => skill.internalSkillName));
					return options;
				};
			}

			AddSkill(Terminal.commands["raiseskill"]);
			AddSkill(Terminal.commands["resetskill"]);
		}
	}

	private static byte[] ReadEmbeddedFileBytes(string name)
	{
		using MemoryStream stream = new();
		Assembly.GetExecutingAssembly().GetManifestResourceStream("DualWield." + name)!.CopyTo(stream);
		return stream.ToArray();
	}

	private static Texture2D loadTexture(string name)
	{
		Texture2D texture = new(0, 0);
		texture.LoadImage(ReadEmbeddedFileBytes("icons." + name));
		return texture;
	}

	public static Sprite loadSprite(string name, int width, int height) => Sprite.Create(loadTexture(name), new Rect(0, 0, width, height), Vector2.zero);
}
