﻿// Saves Character Data in a SQLite database. We use SQLite for serveral reasons
//
// - SQLite is file based and works without having to setup a database server
//   - We can 'remove all ...' or 'modify all ...' easily via SQL queries
//   - A lot of people requested a SQL database and weren't comfortable with XML
//   - We can allow all kinds of character names, even chinese ones without
//     breaking the file system.
// - We will need MYSQL or similar when using multiple server instances later
//   and upgrading is trivial
// - XML is easier, but:
//   - we can't easily read 'just the class of a character' etc., but we need it
//     for character selection etc. often
//   - if each account is a folder that contains players, then we can't save
//     additional account info like password, banned, etc. unless we use an
//     additional account.xml file, which overcomplicates everything
//   - there will always be forbidden file names like 'COM', which will cause
//     problems when people try to create accounts or characters with that name
//
// Tools to open sqlite database files:
//   Windows/OSX program: http://sqlitebrowser.org/
//   Firefox extension: https://addons.mozilla.org/de/firefox/addon/sqlite-manager/
//   Webhost: Adminer/PhpLiteAdmin
//
//
// Build notes:
// - requires Player settings to be set to '.NET' instead of '.NET Subset',
//   otherwise System.Data.dll causes ArgumentException.
// - requires sqlite3.dll x86 and x64 version for standalone (windows/mac/linux)
//   => found on sqlite.org website
// - requires libsqlite3.so x86 and armeabi-v7a for android
//   => compiled from sqlite.org amalgamation source with android ndk r9b linux
using UnityEngine;
using Mirror;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Mono.Data.Sqlite; // copied from Unity/Mono/lib/mono/2.0 to Plugins

public partial class Database
{
    // database path: Application.dataPath is always relative to the project,
    // but we don't want it inside the Assets folder in the Editor (git etc.),
    // instead we put it above that.
    // we also use Path.Combine for platform independent paths
    // and we need persistentDataPath on android
#if UNITY_EDITOR
    static string path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Database.sqlite");
#elif UNITY_ANDROID
    static string path = Path.Combine(Application.persistentDataPath, "Database.sqlite");
#elif UNITY_IOS
    static string path = Path.Combine(Application.persistentDataPath, "Database.sqlite");
#else
    static string path = Path.Combine(Application.dataPath, "Database.sqlite");
#endif

    static SqliteConnection connection;

    // constructor /////////////////////////////////////////////////////////////
    static Database()
    {
        // create database file if it doesn't exist yet
        if (!File.Exists(path))
            SqliteConnection.CreateFile(path);

        // open connection
        connection = new SqliteConnection("URI=file:" + path);
        connection.Open();

        // create tables if they don't exist yet or were deleted
        // [PRIMARY KEY is important for performance: O(log n) instead of O(n)]
        ExecuteNonQuery(@"CREATE TABLE IF NOT EXISTS characters (
                            name TEXT NOT NULL PRIMARY KEY,
                            account TEXT NOT NULL,
                            class TEXT NOT NULL,
                            x REAL NOT NULL,
                            y REAL NOT NULL,
                            z REAL NOT NULL,
                            level INTEGER NOT NULL,
                            health INTEGER NOT NULL,
                            mana INTEGER NOT NULL,
                            strength INTEGER NOT NULL,
                            intelligence INTEGER NOT NULL,
                            experience INTEGER NOT NULL,
                            skillExperience INTEGER NOT NULL,
                            gold INTEGER NOT NULL,
                            online TEXT NOT NULL,
                            deleted INTEGER NOT NULL)");

        // [PRIMARY KEY is important for performance: O(log n) instead of O(n)]
        ExecuteNonQuery(@"CREATE TABLE IF NOT EXISTS character_inventory (
                            character TEXT NOT NULL,
                            slot INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            amount INTEGER NOT NULL,
                            PRIMARY KEY(character, slot))");

        // [PRIMARY KEY is important for performance: O(log n) instead of O(n)]
        ExecuteNonQuery(@"CREATE TABLE IF NOT EXISTS character_equipment (
                            character TEXT NOT NULL,
                            slot INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            amount INTEGER NOT NULL,
                            PRIMARY KEY(character, slot))");

        // [PRIMARY KEY is important for performance: O(log n) instead of O(n)]
        ExecuteNonQuery(@"CREATE TABLE IF NOT EXISTS character_skills (
                            character TEXT NOT NULL,
                            name TEXT NOT NULL,
                            level INTEGER NOT NULL,
                            castTimeEnd REAL NOT NULL,
                            cooldownEnd REAL NOT NULL,
                            PRIMARY KEY(character, name))");

        // [PRIMARY KEY is important for performance: O(log n) instead of O(n)]
        ExecuteNonQuery(@"CREATE TABLE IF NOT EXISTS character_buffs (
                            character TEXT NOT NULL,
                            name TEXT NOT NULL,
                            level INTEGER NOT NULL,
                            buffTimeEnd REAL NOT NULL,
                            PRIMARY KEY(character, name))");
        
        // [PRIMARY KEY is important for performance: O(log n) instead of O(n)]
        ExecuteNonQuery(@"CREATE TABLE IF NOT EXISTS character_quests (
                            character TEXT NOT NULL,
                            name TEXT NOT NULL,
                            field0 INTEGER NOT NULL,
                            completed INTEGER NOT NULL,
                            PRIMARY KEY(character, name))");


        // [PRIMARY KEY is important for performance: O(log n) instead of O(n)]
        ExecuteNonQuery(@"CREATE TABLE IF NOT EXISTS accounts (
                            name TEXT NOT NULL PRIMARY KEY,
                            password TEXT NOT NULL,
                            banned INTEGER NOT NULL)");

        // addon system hooks
        Utils.InvokeMany(typeof(Database), null, "Initialize_");

        Debug.Log("connected to database");
    }

    // helper functions ////////////////////////////////////////////////////////
    // run a query that doesn't return anything
    public static void ExecuteNonQuery(string sql, params SqliteParameter[] args)
    {
        using (SqliteCommand command = new SqliteCommand(sql, connection))
        {
            foreach (SqliteParameter param in args)
                command.Parameters.Add(param);
            command.ExecuteNonQuery();
        }
    }

    // run a query that returns a single value
    public static object ExecuteScalar(string sql, params SqliteParameter[] args)
    {
        using (SqliteCommand command = new SqliteCommand(sql, connection))
        {
            foreach (SqliteParameter param in args)
                command.Parameters.Add(param);
            return command.ExecuteScalar();
        }
    }

    // run a query that returns several values
    // note: sqlite has long instead of int, so use Convert.ToInt32 etc.
    public static List<List<object>> ExecuteReader(string sql, params SqliteParameter[] args)
    {
        List<List<object>> result = new List<List<object>>();

        using (SqliteCommand command = new SqliteCommand(sql, connection))
        {
            foreach (SqliteParameter param in args)
                command.Parameters.Add(param);

            using (SqliteDataReader reader = command.ExecuteReader())
            {
                // the following code causes a SQL EntryPointNotFoundException
                // because sqlite3_column_origin_name isn't found on OSX and
                // some other platforms. newer mono versions have a workaround,
                // but as long as Unity doesn't update, we will have to work
                // around it manually. see also GetSchemaTable function:
                // https://github.com/mono/mono/blob/master/mcs/class/Mono.Data.Sqlite/Mono.Data.Sqlite_2.0/SQLiteDataReader.cs
                //
                //result.Load(reader); (DataTable)
                while (reader.Read())
                {
                    object[] buffer = new object[reader.FieldCount];
                    reader.GetValues(buffer);
                    result.Add(buffer.ToList());
                }
            }
        }

        return result;
    }

    // account data ////////////////////////////////////////////////////////////
    public static bool IsValidAccount(string account, string password)
    {
        // this function can be used to verify account credentials in a database
        // or a content management system.
        //
        // for example, we could setup a content management system with a forum,
        // news, shop etc. and then use a simple HTTP-GET to check the account
        // info, for example:
        //
        //   var request = new WWW("example.com/verify.php?id="+id+"&amp;pw="+pw);
        //   while (!request.isDone)
        //       print("loading...");
        //   return request.error == null && request.text == "ok";
        //
        // where verify.php is a script like this one:
        //   <?php
        //   // id and pw set with HTTP-GET?
        //   if (isset($_GET['id']) && isset($_GET['pw'])) {
        //       // validate id and pw by using the CMS, for example in Drupal:
        //       if (user_authenticate($_GET['id'], $_GET['pw']))
        //           echo "ok";
        //       else
        //           echo "invalid id or pw";
        //   }
        //   ?>
        //
        // or we could check in a MYSQL database:
        //   var dbConn = new MySql.Data.MySqlClient.MySqlConnection("Persist Security Info=False;server=localhost;database=notas;uid=root;password=" + dbpwd);
        //   var cmd = dbConn.CreateCommand();
        //   cmd.CommandText = "SELECT id FROM accounts WHERE id='" + account + "' AND pw='" + password + "'";
        //   dbConn.Open();
        //   var reader = cmd.ExecuteReader();
        //   if (reader.Read())
        //       return reader.ToString() == account;
        //   return false;
        //
        // as usual, we will use the simplest solution possible:
        // create account if not exists, compare password otherwise.
        // no CMS communication necessary and good enough for an Indie MMORPG.

        // not empty?
        if (!Utils.IsNullOrWhiteSpace(account) && !Utils.IsNullOrWhiteSpace(password))
        {
            List<List<object>> table = ExecuteReader("SELECT password, banned FROM accounts WHERE name=@name", new SqliteParameter("@name", account));
            if (table.Count == 1)
            {
                // account exists. check password and ban status.
                List<object> row = table[0];
                return (string)row[0] == password && (long)row[1] == 0;
            }
            else
            {
                // account doesn't exist. create it.
                ExecuteNonQuery("INSERT INTO accounts VALUES (@name, @password, 0)", new SqliteParameter("@name", account), new SqliteParameter("@password", password));
                return true;
            }
        }
        return false;
    }

    // character data //////////////////////////////////////////////////////////
    public static bool CharacterExists(string characterName)
    {
        // checks deleted ones too so we don't end up with duplicates if we un-
        // delete one
        return ((long)ExecuteScalar("SELECT Count(*) FROM characters WHERE name=@name", new SqliteParameter("@name", characterName))) == 1;
    }

    public static void CharacterDelete(string characterName)
    {
        // soft delete the character so it can always be restored later
        ExecuteNonQuery("UPDATE characters SET deleted=1 WHERE name=@character", new SqliteParameter("@character", characterName));
    }

    // returns the list of character names for that account
    // => all the other values can be read with CharacterLoad!
    public static List<string> CharactersForAccount(string account)
    {
        List<string> result = new List<string>();
        List<List<object>> table = ExecuteReader("SELECT name FROM characters WHERE account=@account AND deleted=0", new SqliteParameter("@account", account));
        foreach (List<object> row in table)
            result.Add((string)row[0]);
        return result;
    }

    static void LoadInventory(Player player)
    {
        // fill all slots first
        for (int i = 0; i < player.inventorySize; ++i)
            player.inventory.Add(new ItemSlot());

        // then load valid items and put into their slots
        // (one big query is A LOT faster than querying each slot separately)
        List<List<object>> table = ExecuteReader("SELECT name, slot, amount FROM character_inventory WHERE character=@character", new SqliteParameter("@character", player.name));
        foreach (List<object> row in table)
        {
            string itemName = (string)row[0];
            int slot = Convert.ToInt32((long)row[1]);
            if (slot < player.inventorySize)
            {
                ScriptableItem itemData;
                if (ScriptableItem.dict.TryGetValue(itemName.GetStableHashCode(), out itemData))
                {
                    Item item = new Item(itemData);
                    int amount = Convert.ToInt32((long)row[2]);
                    player.inventory[slot] = new ItemSlot(item, amount); ;
                }
                else Debug.LogWarning("LoadInventory: skipped item " + itemName + " for " + player.name + " because it doesn't exist anymore. If it wasn't removed intentionally then make sure it's in the Resources folder.");
            }
            else Debug.LogWarning("LoadInventory: skipped slot " + slot + " for " + player.name + " because it's bigger than size " + player.inventorySize);
        }
    }

    static void LoadEquipment(Player player)
    {
        // fill all slots first
        for (int i = 0; i < player.equipmentInfo.Length; ++i)
            player.equipment.Add(new ItemSlot());

        // then load valid equipment and put into their slots
        // (one big query is A LOT faster than querying each slot separately)
        List<List<object>> table = ExecuteReader("SELECT name, slot, amount FROM character_equipment WHERE character=@character", new SqliteParameter("@character", player.name));
        foreach (List<object> row in table)
        {
            string itemName = (string)row[0];
            int slot = Convert.ToInt32((long)row[1]);
            ScriptableItem itemData;
            if (slot < player.equipmentInfo.Length)
            {
                if (ScriptableItem.dict.TryGetValue(itemName.GetStableHashCode(), out itemData))
                {
                    Item item = new Item(itemData);
                    int amount = Convert.ToInt32((long)row[2]);
                    player.equipment[slot] = new ItemSlot(item, amount);
                }
                else Debug.LogWarning("LoadEquipment: skipped item " + itemName + " for " + player.name + " because it doesn't exist anymore. If it wasn't removed intentionally then make sure it's in the Resources folder.");
            }
            else Debug.LogWarning("LoadEquipment: skipped slot " + slot + " for " + player.name + " because it's bigger than size " + player.inventorySize);
        }
    }

    static void LoadSkills(Player player)
    {
        // load skills based on skill templates (the others don't matter)
        // -> this way any skill changes in a prefab will be applied
        //    to all existing players every time (unlike item templates
        //    which are only for newly created characters)

        // fill all slots first
        foreach (ScriptableSkill skillData in player.skillTemplates)
            player.skills.Add(new Skill(skillData));

        // then load learned skills and put into their slots
        // (one big query is A LOT faster than querying each slot separately)
        List<List<object>> table = ExecuteReader("SELECT name, level, castTimeEnd, cooldownEnd FROM character_skills WHERE character=@character", new SqliteParameter("@character", player.name));
        foreach (List<object> row in table)
        {
            string skillName = (string)row[0];
            int index = player.skills.FindIndex(skill => skill.name == skillName);
            if (index != -1)
            {
                Skill skill = player.skills[index];
                // make sure that 1 <= level <= maxlevel (in case we removed a skill
                // level etc)
                skill.level = Mathf.Clamp(Convert.ToInt32((long)row[1]), 1, skill.maxLevel);
                // make sure that 1 <= level <= maxlevel (in case we removed a skill
                // level etc)
                // castTimeEnd and cooldownEnd are based on NetworkTime.time
                // which will be different when restarting a server, hence why
                // we saved them as just the remaining times. so let's convert
                // them back again.
                skill.castTimeEnd = (float)row[2] + NetworkTime.time;
                skill.cooldownEnd = (float)row[3] + NetworkTime.time;

                player.skills[index] = skill;
            }
        }
    }

    static void LoadBuffs(Player player)
    {
        // load buffs
        // note: no check if we have learned the skill for that buff
        //       since buffs may come from other people too
        List<List<object>> table = ExecuteReader("SELECT name, level, buffTimeEnd FROM character_buffs WHERE character=@character", new SqliteParameter("@character", player.name));
        foreach (List<object> row in table)
        {
            string buffName = (string)row[0];
            ScriptableSkill skillData;
            if (ScriptableSkill.dict.TryGetValue(buffName.GetStableHashCode(), out skillData))
            {
                // make sure that 1 <= level <= maxlevel (in case we removed a skill
                // level etc)
                int level = Mathf.Clamp(Convert.ToInt32((long)row[1]), 1, skillData.maxLevel);
                Buff buff = new Buff((BuffSkill)skillData, level);
                // buffTimeEnd is based on NetworkTime.time, which will be
                // different when restarting a server, hence why we saved
                // them as just the remaining times. so let's convert them
                // back again.
                buff.buffTimeEnd = (float)row[2] + NetworkTime.time;
                player.buffs.Add(buff);
            }
            else Debug.LogWarning("LoadBuffs: skipped buff " + skillData.name + " for " + player.name + " because it doesn't exist anymore. If it wasn't removed intentionally then make sure it's in the Resources folder.");
        }
    }
    
    static void LoadQuests(Player player)
    {
        // load quests
        List<List<object>> table = ExecuteReader("SELECT name, field0, completed FROM character_quests WHERE character=@character", new SqliteParameter("@character", player.name));
        foreach (List<object> row in table)
        {
            string questName = (string)row[0];
            ScriptableQuest questData;
            if (ScriptableQuest.dict.TryGetValue(questName.GetStableHashCode(), out questData))
            {
                Quest quest = new Quest(questData);
                quest.field0 = Convert.ToInt32((long)row[1]);
                quest.completed = ((long)row[2]) != 0; // sqlite has no bool
                player.quests.Add(quest);
            }
            else Debug.LogWarning("LoadQuests: skipped quest " + questData.name + " for " + player.name + " because it doesn't exist anymore. If it wasn't removed intentionally then make sure it's in the Resources folder.");
        }
    }
    

    public static GameObject CharacterLoad(string characterName, List<Player> prefabs)
    {
        List<List<object>> table = ExecuteReader("SELECT * FROM characters WHERE name=@name AND deleted=0", new SqliteParameter("@name", characterName));
        if (table.Count == 1)
        {
            List<object> mainrow = table[0];

            // instantiate based on the class name
            string className = (string)mainrow[2];
            Player prefab = prefabs.Find(p => p.name == className);
            if (prefab != null)
            {
                GameObject go = GameObject.Instantiate(prefab.gameObject);
                Player player = go.GetComponent<Player>();

                player.name = (string)mainrow[0];
                player.account = (string)mainrow[1];
                player.className = (string)mainrow[2];
                float x = (float)mainrow[3];
                float y = (float)mainrow[4];
                float z = (float)mainrow[5];
                Vector3 position = new Vector3(x, y, z);
                player.level = Convert.ToInt32((long)mainrow[6]);
                int health = Convert.ToInt32((long)mainrow[7]);
                int mana = Convert.ToInt32((long)mainrow[8]);
                player.strength = Convert.ToInt32((long)mainrow[9]);
                player.intelligence = Convert.ToInt32((long)mainrow[10]);
                player.experience = (long)mainrow[11];
                player.skillExperience = (long)mainrow[12];
                player.gold = (long)mainrow[13];

                // try to warp to loaded position.
                // => agent.warp is recommended over transform.position and
                //    avoids all kinds of weird bugs
                // => warping might fail if we changed the world since last save
                //    so we reset to start position if not on navmesh
                player.agent.Warp(position);
                if (!player.agent.isOnNavMesh)
                {
                    Transform start = NetworkManager.singleton.GetNearestStartPosition(position);
                    player.agent.Warp(start.position);
                    Debug.Log(player.name + " invalid position was reset");
                }

                LoadInventory(player);
                LoadEquipment(player);
                LoadSkills(player);
                LoadBuffs(player);
                LoadQuests(player);


                // assign health / mana after max values were fully loaded
                // (they depend on equipment, buffs, etc.)
                player.health = health;
                player.mana = mana;

                // addon system hooks
                Utils.InvokeMany(typeof(Database), null, "CharacterLoad_", player);

                return go;
            }
            else Debug.LogError("no prefab found for class: " + className);
        }
        return null;
    }

    static void SaveInventory(Player player)
    {
        // inventory: remove old entries first, then add all new ones
        // (we could use UPDATE where slot=... but deleting everything makes
        //  sure that there are never any ghosts)
        ExecuteNonQuery("DELETE FROM character_inventory WHERE character=@character", new SqliteParameter("@character", player.name));
        for (int i = 0; i < player.inventory.Count; ++i)
        {
            ItemSlot slot = player.inventory[i];
            if (slot.amount > 0) // only relevant items to save queries/storage/time
                ExecuteNonQuery("INSERT INTO character_inventory VALUES (@character, @slot, @name, @amount)",
                                new SqliteParameter("@character", player.name),
                                new SqliteParameter("@slot", i),
                                new SqliteParameter("@name", slot.item.name),
                                new SqliteParameter("@amount", slot.amount));
        }
    }

    static void SaveEquipment(Player player)
    {
        // equipment: remove old entries first, then add all new ones
        // (we could use UPDATE where slot=... but deleting everything makes
        //  sure that there are never any ghosts)
        ExecuteNonQuery("DELETE FROM character_equipment WHERE character=@character", new SqliteParameter("@character", player.name));
        for (int i = 0; i < player.equipment.Count; ++i)
        {
            ItemSlot slot = player.equipment[i];
            if (slot.amount > 0) // only relevant equip to save queries/storage/time
                ExecuteNonQuery("INSERT INTO character_equipment VALUES (@character, @slot, @name, @amount)",
                                new SqliteParameter("@character", player.name),
                                new SqliteParameter("@slot", i),
                                new SqliteParameter("@name", slot.item.name),
                                new SqliteParameter("@amount", slot.amount));
        }
    }

    static void SaveSkills(Player player)
    {
        // skills: remove old entries first, then add all new ones
        ExecuteNonQuery("DELETE FROM character_skills WHERE character=@character", new SqliteParameter("@character", player.name));
        foreach (Skill skill in player.skills)
            if (skill.level > 0) // only learned skills to save queries/storage/time
                // castTimeEnd and cooldownEnd are based on NetworkTime.time,
                // which will be different when restarting the server, so let's
                // convert them to the remaining time for easier save & load
                // note: this does NOT work when trying to save character data
                //       shortly before closing the editor or game because
                //       NetworkTime.time is 0 then.
                ExecuteNonQuery("INSERT INTO character_skills VALUES (@character, @name, @level, @castTimeEnd, @cooldownEnd)",
                                new SqliteParameter("@character", player.name),
                                new SqliteParameter("@name", skill.name),
                                new SqliteParameter("@level", skill.level),
                                new SqliteParameter("@castTimeEnd", skill.CastTimeRemaining()),
                                new SqliteParameter("@cooldownEnd", skill.CooldownRemaining()));
    }

    static void SaveBuffs(Player player)
    {
        // buffs: remove old entries first, then add all new ones
        ExecuteNonQuery("DELETE FROM character_buffs WHERE character=@character", new SqliteParameter("@character", player.name));
        foreach (Buff buff in player.buffs)
            // buffTimeEnd is based on NetworkTime.time, which will be different
            // when restarting the server, so let's convert them to the
            // remaining time for easier save & load
            // note: this does NOT work when trying to save character data
            //       shortly before closing the editor or game because
            //       NetworkTime.time is 0 then.
            ExecuteNonQuery("INSERT INTO character_buffs VALUES (@character, @name, @level, @buffTimeEnd)",
                            new SqliteParameter("@character", player.name),
                            new SqliteParameter("@name", buff.name),
                            new SqliteParameter("@level", buff.level),
                            new SqliteParameter("@buffTimeEnd", buff.BuffTimeRemaining()));
    }
    
    static void SaveQuests(Player player)
    {
        // quests: remove old entries first, then add all new ones
        ExecuteNonQuery("DELETE FROM character_quests WHERE character=@character", new SqliteParameter("@character", player.name));
        foreach (Quest quest in player.quests)
            ExecuteNonQuery("INSERT INTO character_quests VALUES (@character, @name, @field0, @completed)",
                            new SqliteParameter("@character", player.name),
                            new SqliteParameter("@name", quest.name),
                            new SqliteParameter("@field0", quest.field0),
                            new SqliteParameter("@completed", Convert.ToInt32(quest.completed)));
    }
    
    // adds or overwrites character data in the database
    public static void CharacterSave(Player player, bool online, bool useTransaction = true)
    {
        // only use a transaction if not called within SaveMany transaction
        if (useTransaction) ExecuteNonQuery("BEGIN");

        // online status:
        //   '' if offline (if just logging out etc.)
        //   current time otherwise
        // -> this way it's fault tolerant because external applications can
        //    check if online != '' and if time difference < saveinterval
        // -> online time is useful for network zones (server<->server online
        //    checks), external websites which render dynamic maps, etc.
        // -> it uses the ISO 8601 standard format
        string onlineString = online ? DateTime.UtcNow.ToString("s") : "";

        ExecuteNonQuery("INSERT OR REPLACE INTO characters VALUES (@name, @account, @class, @x, @y, @z, @level, @health, @mana, @strength, @intelligence, @experience, @skillExperience, @gold, @online, 0)",
                        new SqliteParameter("@name", player.name),
                        new SqliteParameter("@account", player.account),
                        new SqliteParameter("@class", player.className),
                        new SqliteParameter("@x", player.transform.position.x),
                        new SqliteParameter("@y", player.transform.position.y),
                        new SqliteParameter("@z", player.transform.position.z),
                        new SqliteParameter("@level", player.level),
                        new SqliteParameter("@health", player.health),
                        new SqliteParameter("@mana", player.mana),
                        new SqliteParameter("@strength", player.strength),
                        new SqliteParameter("@intelligence", player.intelligence),
                        new SqliteParameter("@experience", player.experience),
                        new SqliteParameter("@skillExperience", player.skillExperience),
                        new SqliteParameter("@gold", player.gold),
                        new SqliteParameter("@online", onlineString));

        SaveInventory(player);
        SaveEquipment(player);
        SaveSkills(player);
        SaveBuffs(player);
        SaveQuests(player);

        // addon system hooks
        Utils.InvokeMany(typeof(Database), null, "CharacterSave_", player);

        if (useTransaction) ExecuteNonQuery("END");
    }

    // save multiple characters at once (useful for ultra fast transactions)
    public static void CharacterSaveMany(List<Player> players, bool online = true)
    {
        ExecuteNonQuery("BEGIN"); // transaction for performance
        foreach (Player player in players)
            CharacterSave(player, online, false);
        ExecuteNonQuery("END");
    }
}