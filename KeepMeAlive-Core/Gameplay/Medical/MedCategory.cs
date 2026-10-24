namespace KeepMeAlive.Gameplay.Medical
{
    //====================[ MedCategory ]====================
    // The five kinds of help a healer can give, in the order (and fixed places) the team-heal wheel shows them.
    public enum MedCategory
    {
        Bleeds,   // treats LightBleeding / HeavyBleeding
        Breaks,   // treats Fracture or DestroyedPart (splints, CMS, SURV12)
        Health,   // restores HP (medkit pool, direct HP effect or a HealthRate stim buff)
        Comfort,  // relieves Pain / Contusion / Intoxication / Poisoning
        Nutrition // food and drinks only (restores Energy / Hydration)
    }
}
