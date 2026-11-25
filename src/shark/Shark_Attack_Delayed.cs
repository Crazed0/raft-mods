using UnityEngine;
using System.Collections.Generic;
using HarmonyLib;

public class Shark_Attack_Delayed : Mod
{
    private Harmony harmonyInstance;
    public static List<Shark_Attack_Delayed_Stats> allSharkStats = new List<Shark_Attack_Delayed_Stats>();

    public void Start()
    {
        harmonyInstance = new Harmony("com.nephlymio.sharkattackdelayed");
        harmonyInstance.PatchAll();
        allSharkStats = new List<Shark_Attack_Delayed_Stats>();
        Debug.Log("Mod Shark_Attack_Delayed 1.2.1 (Patched) has been loaded!");
    }

    public void OnModUnload()
    {
        allSharkStats.Clear();
        harmonyInstance.UnpatchAll("com.nephlymio.sharkattackdelayed");
        Debug.Log("Mod Shark_Attack_Delayed has been unloaded!");
    }

    public override void WorldEvent_WorldLoaded()
    {
        allSharkStats = new List<Shark_Attack_Delayed_Stats>();
        Network_Player curPlayer = RAPI.GetLocalPlayer();
        if (curPlayer != null) allSharkStats.Add(new Shark_Attack_Delayed_Stats(curPlayer));
    }

    public override void WorldEvent_WorldUnloaded()
    {
        allSharkStats.Clear();
    }

    public void WorldEvent_OnPlayerConnected(ulong steamid, RGD_Settings_Character characterSettings)
    {
        foreach (var p in Object.FindObjectsOfType<Network_Player>())
        {
            if (p.steamID.ToString() == steamid.ToString())
            {
                allSharkStats.Add(new Shark_Attack_Delayed_Stats(p));
                break;
            }
        }
    }

    public void WorldEvent_OnPlayerDisconnected(ulong steamid, DisconnectReason disconnectReason)
    {
        allSharkStats.RemoveAll(stat => stat.player == null || stat.player.steamID.ToString() == steamid.ToString());
    }

    public void Update()
    {
        // CAMBIO CRÍTICO AQUÍ: Usamos Network_Host para llegar al reloj del cielo
        if (Raft_Network.IsHost && LoadSceneManager.IsGameSceneLoaded)
        {
            var host = ComponentManager<Network_Host>.Value;
            if (host != null && host.skyController != null)
            {
                float currentHour = host.skyController.timeOfDay.hour;
                int activehour = (int)currentHour;

                for (int i = allSharkStats.Count - 1; i >= 0; i--)
                {
                    if (allSharkStats[i].player == null) allSharkStats.RemoveAt(i);
                    else allSharkStats[i].Update(activehour);
                }
            }
        }
    }
}

public class Shark_Attack_Delayed_Stats
{
    public float timeInWater;
    public float attackWait;
    public float timeOnLand;
    public bool resetshark;
    public bool sharkCanAttack;
    public Network_Player player;

    public Shark_Attack_Delayed_Stats(Network_Player parent)
    {
        player = parent;
        timeInWater = 0;
        timeOnLand = 0;
        attackWait = 0;
        resetshark = false;
        sharkCanAttack = false;
    }

    public void Update(int activehour)
    {
        if (player == null || player.PersonController == null) return;
        
        float timePassed = Time.deltaTime;
        if (player.PersonController.SubmersionState == UltimateWater.SubmersionState.None) 
        {
            if (timeInWater != 0)
            {
                timeOnLand += timePassed;
                if (timeOnLand > 4) 
                {
                    timeInWater -= timePassed;
                    if (timeInWater <= 0)
                    {
                        timeInWater = 0;
                        resetshark = false;
                    }
                }    
            }
        }
        else 
        {
            if (timeOnLand != 0) timeOnLand = 0;
            if (timeInWater == 0) attackWait = Random.Range(10f, 20f);
            
            timeInWater += timePassed;

            if (activehour >= 20 || activehour <= 4) 
            { 
                timeInWater += timePassed; 
            }

            if (timeInWater > attackWait)
            {
                timeInWater = 25;
                resetshark = true;
            }          
            sharkCanAttack = resetshark;                
        }
    }
}

[HarmonyPatch(typeof(Helper), "IsValidWaterTarget")]
public class Patch_ValidWaterTargets
{
    static void Postfix(ref bool __result, Network_Player player)
    {
        if (__result && player != null)
        {
            foreach (Shark_Attack_Delayed_Stats stat in Shark_Attack_Delayed.allSharkStats)
            {
                if (stat.player == player)
                {
                    __result = stat.sharkCanAttack;
                    break;
                }
            }
        }
    }
}