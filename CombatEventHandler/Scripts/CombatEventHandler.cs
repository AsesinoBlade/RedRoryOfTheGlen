using System;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Formulas;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallConnect;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.Utility.ModSupport.ModSettings;

public class CombatEventHandler : MonoBehaviour
{
    static Mod mod;
    static int criticalDamageBasePlayer = 2;
    static int criticalDamageBaseEnemy = 2;
    static int playerDivideBy = 5;
    static int enemyDivideBy = 7;
    private static bool showCritMessage = false;
    private static bool IgnoreMaterialOfEnchantedWeapons = true;

    [Invoke(StateManager.StateTypes.Start, 0)]

    public static void Init(InitParams initParams)
    {
        mod = initParams.Mod;
        var go = new GameObject(mod.Title);
        go.AddComponent<CombatEventHandler>();
    }

    public static CombatEventHandler Instance;

    //VCEH - Attack event
    //VCEH - Outputs Attacker, Target, Weapon, Body Part and Damage
    public event Action<DaggerfallEntity, DaggerfallEntity, DaggerfallUnityItem, int, int> OnAttackDamageCalculated;

    //Saving throw event
    //Outputs effect element, effect flags (Paralysis, etc), Target and Result
    public event Action<DFCareer.Elements, DFCareer.EffectFlags, DaggerfallEntity, int> OnSavingThrow;

    void Awake()
    {
        if (Instance == null)
            Instance = this;

        //VCEH - Register the custom formulae
        FormulaHelper.RegisterOverride(mod, "CalculateAttackDamage", (Func<DaggerfallEntity, DaggerfallEntity, bool, int, DaggerfallUnityItem, int>)CalculateAttackDamage);
        FormulaHelper.RegisterOverride(mod, "SavingThrow", (Func<DFCareer.Elements, DFCareer.EffectFlags, DaggerfallEntity, int, int>)SavingThrow);

        //VCEH - Set up the receiver
        mod.MessageReceiver = MessageReceiver;

        mod.IsReady = true;
    }

    void UpdateCriticalHitsSettings()
    {
        Mod chMod = ModManager.Instance.GetMod("Critical Hits");
        if (chMod != null)
        {
            ModSettings settings = chMod.GetSettings();

            criticalDamageBasePlayer = settings.GetValue<int>("criticalHits", "criticalDamageBasePlayer");
            criticalDamageBaseEnemy = settings.GetValue<int>("criticalHits", "criticalDamageBaseEnemy");
            playerDivideBy = settings.GetValue<int>("criticalHits", "playerChance");
            enemyDivideBy = settings.GetValue<int>("criticalHits", "enemyChance");
            showCritMessage = settings.GetBool("criticalHits", "showCritMessage");
            IgnoreMaterialOfEnchantedWeapons = settings.GetValue<bool>("Options", "IgnoreMaterialOfEnchantedWeapons");
        }
    }

    void MessageReceiver(string message, object data, DFModMessageCallback callBack)
    {
        switch (message)
        {
            //VCEH - Add sender to attack event listeners
            case "onAttackDamageCalculated":
                OnAttackDamageCalculated += data as Action<DaggerfallEntity, DaggerfallEntity, DaggerfallUnityItem, int, int>;
                break;

            //VCEH - Add sender to saving throw event listeners
            case "onSavingThrow":
                OnSavingThrow += data as Action<DFCareer.Elements, DFCareer.EffectFlags, DaggerfallEntity, int>;
                break;
            case "UpdateSettings":
                if (data is bool)
                {
                    UpdateCriticalHitsSettings();
                }
                break;

            default:
                Debug.LogErrorFormat("{0}: unknown message received ({1}).", this, message);
                break;
        }
    }

    private static bool IsCriticalStrike(DaggerfallEntity attacker)
    {
        PlayerEntity player = GameManager.Instance.PlayerEntity;
        var attackerLuckBonus = (int)Mathf.Floor(attacker.Stats.LiveLuck / 10);
        var criticalStrikeSkill = attacker.Skills.GetLiveSkillValue(DFCareer.Skills.CriticalStrike);
        var divider = (attacker == player) ? playerDivideBy : enemyDivideBy;
        var criticalChance = (criticalStrikeSkill / divider) + attackerLuckBonus;

#if UNITY_EDITOR
        Debug.LogFormat("{0} Critical chance: {1}. Skill: {2}/{3} + {4}", attacker.Name, criticalChance, criticalStrikeSkill, divider, attackerLuckBonus);
#endif
        return Dice100.SuccessRoll(criticalChance); // Player has a 25% chance of critting at level 100. 32% with 75 luck, and 45% with 100 luck.
    }

    private static int GetCritDamage(int playerSkill, int baseCritDamage)
    {
        var critDamage = playerSkill < 98 ? Mathf.RoundToInt(playerSkill / 25) : 6;

        return critDamage * baseCritDamage;
    }


    //VCEH - Default CalculateAttackDamage formula from FormulaHelper
    //VCEH - Add calls to events before every return
    public static int CalculateAttackDamage(DaggerfallEntity attacker, DaggerfallEntity target, bool isEnemyFacingAwayFromPlayer, int weaponAnimTime, DaggerfallUnityItem weapon)
    {
        if (attacker == null || target == null)
            return 0;

        int damageModifiers = 0;
        int damage = 0;
        int chanceToHitMod = 0;
        int backstabChance = 0;
        PlayerEntity player = GameManager.Instance.PlayerEntity;
        short skillID = 0;
        bool critSuccess = false;
        int critBonusDamage = 0;
        int critHitAddi = 0;


        // Choose whether weapon-wielding enemies use their weapons or weaponless attacks.
        // In classic, weapon-wielding enemies use the damage values of their weapons
        // instead of their weaponless values.
        // For some enemies this gives lower damage than similar-tier monsters
        // and the weaponless values seems more appropriate, so here
        // enemies will choose to use their weaponless attack if it is more damaging.
        EnemyEntity AIAttacker = attacker as EnemyEntity;
        if (AIAttacker != null && weapon != null)
        {
            int weaponAverage = (weapon.GetBaseDamageMin() + weapon.GetBaseDamageMax()) / 2;
            int noWeaponAverage = (AIAttacker.MobileEnemy.MinDamage + AIAttacker.MobileEnemy.MaxDamage) / 2;

            if (noWeaponAverage > weaponAverage)
            {
                // Use hand-to-hand
                weapon = null;
            }
        }

        if (weapon != null)
        {
            // If the attacker is using a weapon, check if the material is high enough to damage the target
            var isEnchanted = weapon.IsEnchanted ||
                              (weapon.poisonType != Poisons.None && !target.IsImmuneToDisease);
            if (!IgnoreMaterialOfEnchantedWeapons)
                isEnchanted = false;


            if (!isEnchanted && target.MinMetalToHit > (WeaponMaterialTypes)weapon.NativeMaterialValue)
            {
                if (attacker == player)
                {
                    DaggerfallUI.Instance.PopupMessage(TextManager.Instance.GetLocalizedText("materialIneffective"));
                }

                //VCEH - Attack event start
                if (Instance.OnAttackDamageCalculated != null)
                    Instance.OnAttackDamageCalculated(attacker, target, weapon, 0, -1);
                //VCEH - Attack event end

                return 0;
            }
            // Get weapon skill used
            skillID = weapon.GetWeaponSkillIDAsShort();
        }
        else
        {
            skillID = (short)DFCareer.Skills.HandToHand;
        }

        chanceToHitMod = attacker.Skills.GetLiveSkillValue(skillID);
        // Handle critical hit damage


        critSuccess = IsCriticalStrike(attacker);

        if (critSuccess)
        {
            int criticalStrikeSkill = attacker.Skills.GetLiveSkillValue(DFCareer.Skills.CriticalStrike);
            critHitAddi = (criticalStrikeSkill / 4);
            chanceToHitMod += critHitAddi;
            if (attacker == player)
            {
                if (showCritMessage) DaggerfallUI.Instance.PopupMessage("You strike with precision!");
                critBonusDamage = GetCritDamage(criticalStrikeSkill, criticalDamageBasePlayer);
            }
            else
            {
                critBonusDamage = GetCritDamage(criticalStrikeSkill, criticalDamageBaseEnemy);
            }
#if UNITY_EDITOR
            Debug.LogFormat("1. critical strike bonus damage: {0}", critBonusDamage);
            Debug.LogFormat("2. critical strike bonus toHit: {0}", critHitAddi);
            Debug.LogFormat("3. Final toHit: {0}", chanceToHitMod);
#endif
        }



        if (attacker == player)
        {
            // Apply swing modifiers
            FormulaHelper.ToHitAndDamageMods swingMods = FormulaHelper.CalculateSwingModifiers(GameManager.Instance.WeaponManager.ScreenWeapon);
            damageModifiers += swingMods.damageMod;
            chanceToHitMod += swingMods.toHitMod;

            // Apply proficiency modifiers
            FormulaHelper.ToHitAndDamageMods proficiencyMods = FormulaHelper.CalculateProficiencyModifiers(attacker, weapon);
            damageModifiers += proficiencyMods.damageMod;
            chanceToHitMod += proficiencyMods.toHitMod;

            // Apply racial bonuses
            FormulaHelper.ToHitAndDamageMods racialMods = FormulaHelper.CalculateRacialModifiers(attacker, weapon, player);
            damageModifiers += racialMods.damageMod;
            chanceToHitMod += racialMods.toHitMod;

            backstabChance = FormulaHelper.CalculateBackstabChance(player, null, isEnemyFacingAwayFromPlayer);
            chanceToHitMod += backstabChance;
        }

        // Choose struck body part
        int struckBodyPart = FormulaHelper.CalculateStruckBodyPart();

        // Get damage for weaponless attacks
        if (skillID == (short)DFCareer.Skills.HandToHand)
        {
            if (attacker == player || (AIAttacker != null && AIAttacker.EntityType == EntityTypes.EnemyClass))
            {
                if (FormulaHelper.CalculateSuccessfulHit(attacker, target, chanceToHitMod, struckBodyPart))
                {
                    damage = FormulaHelper.CalculateHandToHandAttackDamage(attacker, target, damageModifiers, attacker == player);

                    damage = FormulaHelper.CalculateBackstabDamage(damage, backstabChance);
                }
            }
            else if (AIAttacker != null) // attacker is a monster
            {
                // Handle multiple attacks by AI
                int minBaseDamage = 0;
                int maxBaseDamage = 0;
                int attackNumber = 0;
                while (attackNumber < 3) // Classic supports up to 5 attacks but no monster has more than 3
                {
                    if (attackNumber == 0)
                    {
                        minBaseDamage = AIAttacker.MobileEnemy.MinDamage;
                        maxBaseDamage = AIAttacker.MobileEnemy.MaxDamage;
                    }
                    else if (attackNumber == 1)
                    {
                        minBaseDamage = AIAttacker.MobileEnemy.MinDamage2;
                        maxBaseDamage = AIAttacker.MobileEnemy.MaxDamage2;
                    }
                    else if (attackNumber == 2)
                    {
                        minBaseDamage = AIAttacker.MobileEnemy.MinDamage3;
                        maxBaseDamage = AIAttacker.MobileEnemy.MaxDamage3;
                    }

                    int reflexesChance = 50 - (10 * ((int)player.Reflexes - 2));

                    int hitDamage = 0;
                    if (DFRandom.rand() % 100 < reflexesChance && minBaseDamage > 0 && FormulaHelper.CalculateSuccessfulHit(attacker, target, chanceToHitMod, struckBodyPart))
                    {
                        hitDamage = UnityEngine.Random.Range(minBaseDamage, maxBaseDamage + 1);
                        // Apply special monster attack effects
                        if (hitDamage > 0)
                            FormulaHelper.OnMonsterHit(AIAttacker, target, hitDamage);

                        damage += hitDamage;
                    }

                    // Apply bonus damage only when monster has actually hit, or they will accumulate bonus damage even for missed attacks and zero-damage attacks
                    if (hitDamage > 0)
                        damage += FormulaHelper.GetBonusOrPenaltyByEnemyType(attacker, target);

                    ++attackNumber;
                }
            }
        }
        // Handle weapon attacks
        else if (weapon != null)
        {
            // Apply weapon material modifier.
            chanceToHitMod += FormulaHelper.CalculateWeaponToHit(weapon);

            // Mod hook for adjusting final hit chance mod and adding new elements to calculation. (no-op in DFU)
            chanceToHitMod = FormulaHelper.AdjustWeaponHitChanceMod(attacker, target, chanceToHitMod, weaponAnimTime, weapon);

            if (FormulaHelper.CalculateSuccessfulHit(attacker, target, chanceToHitMod, struckBodyPart))
            {
                damage = FormulaHelper.CalculateWeaponAttackDamage(attacker, target, damageModifiers, weaponAnimTime, weapon);

                damage = FormulaHelper.CalculateBackstabDamage(damage, backstabChance);
            }

            // Handle poisoned weapons
            if (damage > 0 && weapon.poisonType != Poisons.None)
            {
                FormulaHelper.InflictPoison(attacker, target, weapon.poisonType, false);
                if (FormulaHelper.ShouldClearWeaponPoison(attacker, player))
                    FormulaHelper.RemovePoison(attacker, weapon);
            }
        }
#if UNITY_EDITOR
        Debug.LogFormat("Standard Damage = {0}", damage);
#endif

        if (critSuccess)
        {
            damage += critBonusDamage;
#if UNITY_EDITOR
            Debug.LogFormat("Critical damage = {0}", damage);
#endif
        }
        damage = Mathf.Max(0, damage);

        FormulaHelper.DamageEquipment(attacker, target, damage, weapon, struckBodyPart);

        // Apply Ring of Namira effect
        if (target == player)
        {
            DaggerfallUnityItem[] equippedItems = target.ItemEquipTable.EquipTable;
            DaggerfallUnityItem item = null;
            if (equippedItems.Length != 0)
            {
                if (Instance.IsRingOfNamira(equippedItems[(int)EquipSlots.Ring0]) || Instance.IsRingOfNamira(equippedItems[(int)EquipSlots.Ring1]))
                {
                    IEntityEffect effectTemplate = GameManager.Instance.EntityEffectBroker.GetEffectTemplate(RingOfNamiraEffect.EffectKey);
                    effectTemplate.EnchantmentPayloadCallback(EnchantmentPayloadFlags.None,
                        targetEntity: AIAttacker.EntityBehaviour,
                        sourceItem: item,
                        sourceDamage: damage);
                }
            }
        }

        //Debug.LogFormat("Damage {0} applied, animTime={1}  ({2})", damage, weaponAnimTime, GameManager.Instance.WeaponManager.ScreenWeapon.WeaponState);

        //VCEH - Attack event start
        if (Instance.OnAttackDamageCalculated != null)
            Instance.OnAttackDamageCalculated(attacker, target, weapon, struckBodyPart, damage);
        //VCEH - Attack event end

        //Damage dealt
        return damage;
    }

    //Added here as a requirement
    public bool IsRingOfNamira(DaggerfallUnityItem item)
    {
        return item != null && item.ContainsEnchantment(DaggerfallConnect.FallExe.EnchantmentTypes.SpecialArtifactEffect, (int)ArtifactsSubTypes.Ring_of_Namira);
    }

    //VCEH - Default SavingThrow formula from FormulaHelper
    //VCEH - Before the return, pre-calculate the result and call the saving throw event
    public static int SavingThrow(DFCareer.Elements elementType, DFCareer.EffectFlags effectFlags, DaggerfallEntity target, int modifier)
    {
        // Handle resistances granted by magical effects
        if (target.HasResistanceFlag(elementType))
        {
            int chance = target.GetResistanceChance(elementType);
            if (Dice100.SuccessRoll(chance))
                return 0;
        }

        // Magic effect resistances did not stop the effect. Try with career flags and biography modifiers
        int savingThrow = 50;
        DFCareer.ToleranceFlags toleranceFlags = DFCareer.ToleranceFlags.Normal;
        int biographyMod = 0;

        PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
        if ((effectFlags & DFCareer.EffectFlags.Paralysis) != 0)
        {
            toleranceFlags |= FormulaHelper.GetToleranceFlag(target.Career.Paralysis);
            // Innate immunity if high elf. Start with 100 saving throw, but can be modified by
            // tolerance flags. Note this differs from classic, where high elves have 100% immunity
            // regardless of tolerance flags.
            if (target == playerEntity && playerEntity.Race == Races.HighElf)
                savingThrow = 100;
        }
        if ((effectFlags & DFCareer.EffectFlags.Magic) != 0)
        {
            toleranceFlags |= FormulaHelper.GetToleranceFlag(target.Career.Magic);
            if (target == playerEntity)
                biographyMod += playerEntity.BiographyResistMagicMod;
        }
        if ((effectFlags & DFCareer.EffectFlags.Poison) != 0)
        {
            toleranceFlags |= FormulaHelper.GetToleranceFlag(target.Career.Poison);
            if (target == playerEntity)
                biographyMod += playerEntity.BiographyResistPoisonMod;
        }
        if ((effectFlags & DFCareer.EffectFlags.Fire) != 0)
            toleranceFlags |= FormulaHelper.GetToleranceFlag(target.Career.Fire);
        if ((effectFlags & DFCareer.EffectFlags.Frost) != 0)
            toleranceFlags |= FormulaHelper.GetToleranceFlag(target.Career.Frost);
        if ((effectFlags & DFCareer.EffectFlags.Shock) != 0)
            toleranceFlags |= FormulaHelper.GetToleranceFlag(target.Career.Shock);
        if ((effectFlags & DFCareer.EffectFlags.Disease) != 0)
        {
            toleranceFlags |= FormulaHelper.GetToleranceFlag(target.Career.Disease);
            if (target == playerEntity)
                biographyMod += playerEntity.BiographyResistDiseaseMod;
        }

        // Note: Differing from classic implementation here. In classic
        // immune grants always 100% resistance and critical weakness is
        // always 0% resistance if there is no immunity. Here we are using
        // a method that allows mixing different tolerance flags, getting
        // rid of related exploits when creating a character class.
        if ((toleranceFlags & DFCareer.ToleranceFlags.Immune) != 0)
            savingThrow += 50;
        if ((toleranceFlags & DFCareer.ToleranceFlags.CriticalWeakness) != 0)
            savingThrow -= 50;
        if ((toleranceFlags & DFCareer.ToleranceFlags.LowTolerance) != 0)
            savingThrow -= 25;
        if ((toleranceFlags & DFCareer.ToleranceFlags.Resistant) != 0)
            savingThrow += 25;

        savingThrow += biographyMod + modifier;
        if (elementType == DFCareer.Elements.Frost && target == playerEntity && playerEntity.Race == Races.Nord)
            savingThrow += 30;
        else if (elementType == DFCareer.Elements.Magic && target == playerEntity && playerEntity.Race == Races.Breton)
            savingThrow += 30;

        // Handle perfect immunity of 100% or greater
        // Otherwise clamping to 5-95 allows a perfectly immune character to sometimes receive incoming payload
        // This doesn't seem to match immunity intent or player expectations from classic
        if (savingThrow >= 100)
            return 0;

        // Increase saving throw by MagicResist, equal to LiveWillpower / 10 (rounded down)
        savingThrow += target.MagicResist;

        savingThrow = Mathf.Clamp(savingThrow, 5, 95);

        int percentDamageOrDuration = 100;
        int roll = Dice100.Roll();

        if (roll <= savingThrow)
        {
            // Percent damage/duration is prorated at within 20 of failed roll, as described in DF Chronicles
            if (savingThrow - 20 <= roll)
                percentDamageOrDuration = 100 - 5 * (savingThrow - roll);
            else
                percentDamageOrDuration = 0;
        }

        //VCEH - Pre-calculating the result
        int result = Mathf.Clamp(percentDamageOrDuration, 0, 100);

        //VCEH - Saving throw event start
        if (Instance.OnSavingThrow != null)
            Instance.OnSavingThrow(elementType,effectFlags,target,result);
        //VCEH - Saving throw event end

        return result;
    }
}
