﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DeNSo;
using System.Threading;
using System.IO;

#if WINDOWS
using System.Runtime.Serialization.Formatters.Binary;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Runtime.CompilerServices;
#endif


namespace DeNSo
{
  public static class StoreManager
  {
    private static DensoExtensions _extensions = new DensoExtensions();
    private static bool _started = false;

    private static Thread _saveDBThread = null;
    private static Thread _indexerThread = null;

    internal static bool ShuttingDown = false;
    internal static ManualResetEvent ShutDownEvent = new ManualResetEvent(false);

#if WINDOWS_PHONE
    internal static System.IO.IsolatedStorage.IsolatedStorageFile iss = System.IO.IsolatedStorage.IsolatedStorageFile.GetUserStoreForApplication();
#endif


    private volatile static Dictionary<string, Dictionary<string, IObjectStore>> _stores =
              new Dictionary<string, Dictionary<string, IObjectStore>>();

    private volatile static Dictionary<string, EventStore> _eventStore =
              new Dictionary<string, EventStore>();

    static StoreManager()
    {

    }

    public static string[] GetCollections(string databasename)
    {
      if (_stores.ContainsKey(databasename))
        return _stores[databasename].Keys.ToArray();
      return null;
    }

    public static EventStore GetEventStore(string databasename)
    {
      lock (_eventStore)
        if (!_eventStore.ContainsKey(databasename))
          _eventStore.Add(databasename, new EventStore(databasename, 0));

      return _eventStore[databasename];
    }

    public static ObjectStore GetObjectStore(string databasename, string collection)
    {
      lock (_stores)
        if (!_stores.ContainsKey(databasename))
          _stores.Add(databasename, new Dictionary<string, IObjectStore>());

      lock (_stores[databasename])
        if (!_stores[databasename].ContainsKey(collection))
          _stores[databasename].Add(collection, LoadCollection(databasename, collection));

      return _stores[databasename][collection] as ObjectStore;
    }

    public static void Start()
    {
      if (!_started)
      {
        LogWriter.LogInformation("Starting StoreManager", LogEntryType.Warning);
        ShuttingDown = false;
        ShutDownEvent.Reset();

        LogWriter.LogInformation("Initializing Extensions", LogEntryType.Warning);
        // Init all the extensions. 
        _extensions.Init();

        foreach (var db in GetDatabases())
        {
          LogWriter.LogInformation(string.Format("Opening Database {0}", db), LogEntryType.Warning);
          OpenDataBase(db);
        }

        _saveDBThread = new Thread(new ThreadStart(SaveDBThreadMethod));
        _saveDBThread.Start();

        _indexerThread = new Thread(new ThreadStart(CheckBloomIndexes));
        _indexerThread.IsBackground = true;
        _indexerThread.Start();

        LogWriter.LogInformation("Store Manager initialization completed", LogEntryType.SuccessAudit);
        _started = true;
      }
    }

    public static void ShutDown()
    {
      JournalWriter.RaiseCloseEvent();
      ShuttingDown = true;
      ShutDownEvent.Set();
      if (_saveDBThread != null)
        _saveDBThread.Join((int)new TimeSpan(0, 5, 0).TotalMilliseconds);

      // remove all Event Store
      lock (_eventStore)
        _eventStore.Clear();
      lock (_stores)
        _stores.Clear();

      _started = false;
    }

    public static string[] Databases
    {
      get { return _stores.Keys.ToArray(); }
    }

    private static string[] GetDatabases()
    {
      List<string> result = new List<string>();
      if (Directory.Exists(Configuration.BasePath))
      {
        foreach (var dir in Directory.GetDirectories(Configuration.BasePath))
        {
          var dinfo = new DirectoryInfo(dir);
          if (dinfo.Exists)
            result.Add(dinfo.Name);
        }
      }
      return result.ToArray();
    }

    private static void OpenDataBase(string databasename)
    {
      lock (_eventStore)
        if (!_eventStore.ContainsKey(databasename))
        {
          var filename = Path.Combine(Path.Combine(Configuration.BasePath, databasename), "denso.trn");

          long eventcommandsn = 0;
#if WINDOWS_PHONE
        if (iss.FileExists(filename))
          using (var fs = iss.OpenFile(Path.Combine(Path.Combine(Configuration.BasePath, databasename), "denso.trn"), FileMode.Open, FileAccess.Read))
#else
          if (File.Exists(filename))
            using (var fs = File.Open(Path.Combine(Path.Combine(Configuration.BasePath, databasename), "denso.trn"), FileMode.Open, FileAccess.Read, FileShare.Read))
#endif
              if (fs.Length > 0)
                using (var br = new BinaryReader(fs))
                  eventcommandsn = br.ReadInt64();

          _eventStore.Add(databasename, new EventStore(databasename, eventcommandsn));
        }
    }

    private static void SaveDBThreadMethod()
    {
      while (!ShutDownEvent.WaitOne(2))
      {
        ShutDownEvent.WaitOne(Configuration.SaveInterval);
        lock (_stores)
          foreach (var db in _stores.Keys)
          {
            SaveDataBase(db);
            ShutDownEvent.WaitOne(Configuration.DBCheckTimeSpan);
          }
      }
      ShutDownEvent.Reset();
    }

    private static void CheckBloomIndexes()
    {
      while (!ShutDownEvent.WaitOne(Configuration.ReindexCheck))
      {
        var dbkeys = _stores.Keys.ToArray();
        foreach (var d in dbkeys)
        {
          var collkeys = _stores[d].Keys.ToArray();
          foreach (var c in collkeys)
          {
            var errorratio = _stores[d][c].IncoerenceIndexRatio();
            if (errorratio > 1)
            {
              LogWriter.LogInformation(string.Format("Reindexing {0} - {1} - IndexRatio: {2}", d, c, errorratio), LogEntryType.Warning);
              _stores[d][c].Reindex();
              LogWriter.LogInformation(string.Format("Completed {0} - {1}", d, c), LogEntryType.Warning);
            }
          }
        }

      }
    }

    internal static void SaveDataBase(string databasename)
    {
      var collections = GetCollections(databasename);
      foreach (var coll in collections)
        SaveCollection(databasename, coll);

      var es = GetEventStore(databasename);
#if WINDOWS_PHONE
      using (var fs = iss.CreateFile(Path.Combine(Path.Combine(Configuration.BasePath, databasename), "denso.trn")))
#else
      using (var fs = File.Create(Path.Combine(Path.Combine(Configuration.BasePath, databasename), "denso.trn")))
#endif
      using (var bw = new BinaryWriter(fs))
        bw.Write(es.LastExecutedCommandSN);

      es.ShrinkEventStore();
    }

    internal static void SaveCollection(string database, string collection)
    {
      var store = GetObjectStore(database, collection);
      if (store.ChangesFromLastSave > 0)
      {
        var fullpath = Path.Combine(Path.Combine(Configuration.BasePath, database), collection + ".coll");

#if WINDOWS_PHONE
        if (!iss.DirectoryExists(Path.GetDirectoryName(fullpath)))
          iss.CreateDirectory(Path.GetDirectoryName(fullpath));
#else
        if (!Directory.Exists(Path.GetDirectoryName(fullpath)))
          Directory.CreateDirectory(Path.GetDirectoryName(fullpath));
#endif

        using (var file = File.Open(fullpath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
          using (var writer = new BinaryWriter(file))
          {
            foreach (var dstore in store._primarystore)
              foreach (var item in dstore)
              {
                writer.Write((int)item.Value.Length); // Data Lenght
                //writer.Write((byte)item.Key.Length);
                writer.Write(item.Key); // Database _id
                writer.Write(item.Value); // Data
              }
            writer.Flush();
            file.Flush();
            file.SetLength(file.Position);
          }
        }
      }
    }

    internal static ObjectStore LoadCollection(string database, string collection)
    {
      var store = new ObjectStore();
      var fullpath = Path.Combine(Path.Combine(Configuration.BasePath, database), collection + ".coll");
#if WINDOWS_PHONE
      if (iss.FileExists(fullpath))
        using (var fs = iss.OpenFile(fullpath, FileMode.Open, FileAccess.Read, FileShare.None))
#else
      if (File.Exists(fullpath))
        using (var fs = File.Open(fullpath, FileMode.Open, FileAccess.Read, FileShare.Read))
#endif
          try
          {
            using (var br = new BinaryReader(fs))
              while (fs.Position < fs.Length)
              {
                var len = br.ReadInt32();
                //var klen = br.ReadByte();
                var id = br.ReadString();
                var data = br.ReadString();

                store.InternalDictionaryInsert(id, data);
              }
          }
          catch (OutOfMemoryException ex)
          {
            LogWriter.LogException(ex);
          }
      return store;
    }
  }
}
