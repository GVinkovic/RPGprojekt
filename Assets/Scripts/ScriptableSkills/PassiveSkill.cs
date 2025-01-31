﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Skill/Passive Skill", order = 999)]
public class PassiveSkill : BonusSkill
{
    public override bool CheckTarget(Entity caster) { return false; }
    public override bool CheckDistance(Entity caster, int skillLevel, out Vector3 destination)
    {
        destination = caster.transform.position;
        return false;
    }
    public override void Apply(Entity caster, int skillLevel) { }
}