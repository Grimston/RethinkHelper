using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RethinkDb.Driver;
using RethinkDb.Driver.Extras.Dao;
using RethinkDb.Driver.Model;
using RethinkDb.Driver.Net;
using RethinkDb.Driver.Net.Clustering;
using RethinkDb.Driver.Utils;
using RH.Attributes;

namespace RH
{
    public static class RethinkHelper
    {
        public static ConnectionPool ConnectionPool;
        public static string Database;

        public static bool Frozen { get; set; } = false;

        public static void Connect(string[] seeds, string db)
        {
            Database = db;
            ConnectionPool = RethinkDB.R.ConnectionPool()
                .Seed(seeds)
                .PoolingStrategy(new RoundRobinHostPool())
                .Db(db)
                .Discover(true)
                .Connect();
        }

        public static void Connect(string server, string db)
        {
            Connect(new[] {server}, db);
        }

        /// <summary>
        /// This object can load a valid Document type and allow simple modification without the use of a full DAO class
        /// </summary>
        /// <typeparam name="T">The Document type</typeparam>
        /// <returns></returns>
        public static T Dispense<T>() where T : RethinkObject<T>, IDocument<Guid>, new()
        {
            return new T();
        }

        /// <summary>
        /// Saves or updates a document. If the document doesn't exist, it will be saved. If the document exists, it will be updated.
        /// </summary>
        /// <param name="rethinkObject"></param>
        public static Guid Store(object rethinkObject)
        {
            return StoreAsync(rethinkObject).WaitSync();
        }

        /// <summary>
        /// Get a document by Id.
        /// </summary>
        private static object FindOne(Type type, Guid id)
        {
            return GetObject(type, id, type.Name);
        }

        public static T FindOne<T>(string guid) where T : new()
        {
            return FindOne<T>(Guid.Parse(guid));
        }

        /// <summary>
        /// Get a document by Id.
        /// </summary>
        public static T FindOne<T>(Guid id) where T : new()
        {
            return (T) FindOne(typeof(T), id);
        }

        /// <summary>
        /// Saves or updates a document. If the document doesn't exist, it will be saved. If the document exists, it will be updated.
        /// </summary>
        /// <param name="rethinkObject"></param>
        public static async Task<Guid> StoreAsync(object rethinkObject)
        {
            if (rethinkObject == null) return Guid.Empty;

            var tableName = rethinkObject.GetType().Name;

            if (!Frozen) //Handy for quick proto-typing, but would slow down a production build
            {
                _EnsureTablesExist(rethinkObject);
            }

            var obj = new JObject();
            var properties = rethinkObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

            var storeSharedListItems = new List<SharedListItem>(0);

            foreach (var p in properties)
            {
                if (Attribute.IsDefined(p, typeof(NonSerializedAttribute))) continue;

                // If not writable then cannot null it; if not readable then cannot check it's value
                if (!p.CanWrite || !p.CanRead) continue;

                var mget = p.GetGetMethod(false);
                var mset = p.GetSetMethod(false);

                // Get and set methods have to be public
                if (mget == null) continue;

                if (mset == null) continue;


                if (p.PropertyType.IsArray || p.PropertyType.Name == "RethinkArray`1")
                {
                    if (Attribute.IsDefined(p, typeof(RefTableAttribute)))
                    {
                        if (Attribute.IsDefined(p, typeof(SharedTableAttribute)))
                        {
                            var parentName = tableName + "_id";
                            var childTypeName = p.PropertyType.Name == "RethinkArray`1"
                                ? p.PropertyType.GenericTypeArguments[0].Name
                                : p.PropertyType.GetElementType().Name;

                            var childName = childTypeName + "_id";
                            var dbTable = tableName + "_shared_" + childTypeName;

                            storeSharedListItems.AddRange(
                                from object reObject in (IEnumerable) p.GetValue(rethinkObject)
                                select new SharedListItem()
                                {
                                    Table = dbTable,
                                    ChildName = childName,
                                    ParentName = parentName,
                                    ChildGuid = Store(reObject),
                                    SharedId = Guid.Parse(reObject.GetType()
                                        .GetProperty("SharedId", BindingFlags.NonPublic | BindingFlags.Instance)
                                        .GetValue(reObject)
                                        .ToString())
                                });
                        }
                        else
                        {
                            obj[p.Name + "_list"] = new JArray(
                                (from object reObject in (IEnumerable) p.GetValue(rethinkObject)
                                    select Store(reObject)));
                        }
                    }
                    else
                    {
                        obj[p.Name + "_id"] = Store(p.GetValue(rethinkObject));
                    }
                }
                else
                {
                    if (p.Name == "Id")
                    {
                        var idValue = (Guid) p.GetValue(rethinkObject);
                        if (idValue != Guid.Empty)
                        {
                            obj["id"] = new JValue(idValue);
                        }
                    }
                    else
                    {
                        obj[p.Name] = new JValue(p.GetValue(rethinkObject));
                    }
                }
            }

            var result = await RethinkDB.R.Table(tableName)
                .Insert(obj)[new {return_changes = true}].OptArg("conflict", "replace")
                .RunWriteAsync(ConnectionPool)
                .ConfigureAwait(false);

            result.AssertNoErrors();

            //Get old or new key
            var newKey = obj["id"] != null ? (Guid) obj["id"] : result.GeneratedKeys[0];

            //Do we need to go back and fix child items?
            if (storeSharedListItems.Count != 0)
            {
                foreach (var item in storeSharedListItems)
                {
                    var reObject = new JObject {[item.ParentName] = newKey, [item.ChildName] = item.ChildGuid};
                    //Only assign the id if we actually have one. Let RethinkDB generate it.
                    if (item.SharedId != Guid.Empty)
                    {
                        reObject["id"] = item.SharedId;
                    }

                    RethinkDB.R.Table(item.Table)
                        .Insert(reObject)
                        .Run(ConnectionPool);
                }
            }

            return newKey;
        }

        private static void _EnsureTablesExist(object rethinkObject)
        {
            var tableName = rethinkObject.GetType().Name;
            if (rethinkObject.GetType().IsArray)
            {
                tableName = rethinkObject.GetType().GetElementType().Name;
            }

            if (!RethinkDB.R.TableList().Contains(tableName).RunAtom<bool>(ConnectionPool))
            {
                RethinkDB.R.TableCreate(tableName).Run(ConnectionPool);
            }

            foreach (var property in rethinkObject.GetType().GetProperties())
            {
                if (Attribute.IsDefined(property, typeof(SecondaryIndexAttribute)))
                {
                    if (!RethinkDB.R.Table(tableName).IndexList().Contains(property.Name)
                        .RunAtom<bool>(ConnectionPool))
                    {
                        RethinkDB.R.Table(tableName).IndexCreate(property.Name).RunNoReply(ConnectionPool);
                        RethinkDB.R.Table(tableName).IndexWait().Run(ConnectionPool);
                    }
                }

                if (Attribute.IsDefined(property, typeof(RefTableAttribute)))
                {
                    if ((property.PropertyType.IsArray || property.PropertyType.Name == "RethinkArray`1") &&
                        Attribute.IsDefined(property, typeof(SharedTableAttribute)))
                    {
                        var childTypeName = property.PropertyType.Name == "RethinkArray`1"
                            ? property.PropertyType.GenericTypeArguments[0].Name
                            : property.PropertyType.GetElementType().Name;

                        var sharedTable = tableName + "_shared_" + childTypeName;
                        var parentName = tableName + "_id";
                        var childName = childTypeName + "_id";

                        _CreateSharedTable(sharedTable, parentName, childName);
                    }
                    else
                    {
                        if (!RethinkDB.R.Table(tableName).IndexList().Contains(property.Name)
                            .RunAtom<bool>(ConnectionPool))
                        {
                            RethinkDB.R.Table(tableName).IndexCreate(property.Name + "_id")
                                .RunNoReply(ConnectionPool);
                            RethinkDB.R.Table(tableName).IndexWait().Run(ConnectionPool);
                        }
                    }
                }
            }
        }

        private static void _CreateSharedTable(string sharedTable, string parentName, string childName)
        {
            //Shared List Table...
            if (!RethinkDB.R.TableList().Contains(sharedTable).RunAtom<bool>(ConnectionPool))
            {
                RethinkDB.R.TableCreate(sharedTable).Run(ConnectionPool);
            }

            if (!RethinkDB.R.Table(sharedTable).IndexList().Contains(parentName).RunAtom<bool>(ConnectionPool))
            {
                RethinkDB.R.Table(sharedTable).IndexCreate(parentName).RunNoReply(ConnectionPool);
            }

            if (!RethinkDB.R.Table(sharedTable).IndexList().Contains(childName).RunAtom<bool>(ConnectionPool))
            {
                RethinkDB.R.Table(sharedTable).IndexCreate(childName).RunNoReply(ConnectionPool);
            }

            RethinkDB.R.Table(sharedTable).IndexWait().Run(ConnectionPool);
        }

        public static void Trash(object rethinkObject)
        {
            TrashAsync(rethinkObject).WaitSync();
        }

        public static async Task TrashAsync(object rethinkObject)
        {
            var properties = rethinkObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var p in properties)
            {
                if (Attribute.IsDefined(p, typeof(NonSerializedAttribute))) continue;
                if (!p.CanWrite || !p.CanRead) continue;
                var mget = p.GetGetMethod(false);
                var mset = p.GetSetMethod(false);
                // Get and set methods have to be public
                if (mget == null) continue;
                if (mset == null) continue;

                if (Attribute.IsDefined(p, typeof(RefTableAttribute)))
                {
                    if (p.PropertyType.IsArray || p.PropertyType.Name == "RethinkArray`1")
                    {
                        var childTypeName = p.PropertyType.Name == "RethinkArray`1"
                            ? p.PropertyType.GenericTypeArguments[0].Name
                            : p.PropertyType.GetElementType().Name;

                        if (Attribute.IsDefined(p, typeof(SharedTableAttribute)))
                        {
                            var sharedTable = rethinkObject.GetType().Name + "_shared_" + childTypeName;
                            var parentName = rethinkObject.GetType().Name + "_id";
                            RethinkDB.R.Table(sharedTable)
                                .GetAll(rethinkObject.GetType().GetProperty("Id").GetValue(rethinkObject))[
                                    new {index = parentName}].Delete().Run(ConnectionPool);
                        }
                        else
                        {
                            foreach (var reObject in (IEnumerable) p.GetValue(rethinkObject))
                            {
                                await TrashAsync(reObject);
                            }
                        }
                    }
                    else
                    {
                        //Only delete the child item IF we are missing NoTrash
                        if (!Attribute.IsDefined(p, typeof(NoTrashAttribute)))
                        {
                            await TrashAsync(p.GetValue(rethinkObject));
                        }
                    }
                }
                else
                {
                    if (p.Name == "Id")
                    {
                        await RethinkDB.R.Table(rethinkObject.GetType().Name)
                            .Get(p.GetValue(rethinkObject)).Delete()
                            .RunWriteAsync(ConnectionPool)
                            .ConfigureAwait(false);
                    }
                }
            }
        }

        public static void Trash<T>(RethinkObject<T> obj) where T : IDocument<Guid>, new()
        {
            TrashAsync(obj).WaitSync();
        }

        public static async Task TrashAsync<T>(RethinkObject<T> obj) where T : IDocument<Guid>, new()
        {
            await TrashAsync((object) obj);
        }

        /// <summary>
        /// Drop an entire table
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public static void DropTable<T>() where T : RethinkObject<T>, IDocument<Guid>, new()
        {
            DropTable(typeof(T).Name);
        }

        public static void DropTable(string name)
        {
            if (RethinkDB.R.TableList().Contains(name).RunAtom<bool>(ConnectionPool))
            {
                RethinkDB.R.TableDrop(name).Run(ConnectionPool);
            }
        }

        /// <summary>
        /// This method is primarily used for RethinkArray items to support delayed loading of items
        /// </summary>
        /// <param name="type"></param>
        /// <param name="id"></param>
        /// <param name="overideTableName"></param>
        /// <returns></returns>
        internal static object GetObject(Type type, Guid id, string overideTableName = null)
        {
            var tableName = type.Name;
            var targetType = type;
            if (targetType.IsArray)
            {
                tableName = tableName.Replace("[]", "");
                targetType = targetType.GetElementType();
            }

            if (!string.IsNullOrWhiteSpace(overideTableName))
            {
                tableName = overideTableName;
            }

            var jsonObject = RethinkDB.R.Table(tableName).Get(id).RunAtom<Dictionary<string, object>>(ConnectionPool);
            var resultingDocument = Activator.CreateInstance(targetType);
            var properties = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var p in properties)
            {
                if (Attribute.IsDefined(p, typeof(NonSerializedAttribute))) continue;
                if (!p.CanWrite || !p.CanRead) continue;
                var mget = p.GetGetMethod(false);
                var mset = p.GetSetMethod(false);
                if (mget == null) continue;
                if (mset == null) continue;

                if (Attribute.IsDefined(p, typeof(RefTableAttribute)))
                {
                    if (p.PropertyType.IsArray || p.PropertyType.Name == "RethinkArray`1")
                    {
                        var childTypeName = p.PropertyType.Name == "RethinkArray`1"
                            ? p.PropertyType.GenericTypeArguments[0].Name
                            : p.PropertyType.GetElementType().Name;

                        if (Attribute.IsDefined(p, typeof(SharedTableAttribute)))
                        {
                            var parentName = tableName + "_id";
                            var childName = childTypeName + "_id";
                            var dbTable = tableName + "_shared_" + childTypeName;

                            if (p.PropertyType.Name == "RethinkArray`1")
                            {
                                var listType =
                                    typeof(RethinkArray<>).MakeGenericType(p.PropertyType.GenericTypeArguments[0]);
                                var list = Activator.CreateInstance(listType);

                                var method = list.GetType().GetMethod("AddRethinkItem",
                                    BindingFlags.NonPublic | BindingFlags.Instance);

                                foreach (var reObject in RethinkDB.R.Table(dbTable).GetAll(id)[new {index = parentName}]
                                    .RunCursor<JObject>(ConnectionPool))
                                {
                                    method.Invoke(list, new object[]
                                    {
                                        new RethinkItem
                                        {
                                            Id = Guid.Parse(reObject[childName].ToString()),
                                            Table = childTypeName
                                        }
                                    });
                                }

                                p.SetValue(resultingDocument, list);
                            }
                            else
                            {
                                Cursor<JObject> cursor = RethinkDB.R.Table(dbTable).GetAll(id)[new {index = parentName}]
                                    .RunCursor<JObject>(ConnectionPool);

                                var listType = typeof(List<>).MakeGenericType(p.PropertyType.GetElementType());
                                var list = (IList) Activator.CreateInstance(listType);

                                foreach (var reObject in cursor)
                                {
                                    var sharedObject = FindOne(p.PropertyType.GetElementType(),
                                        Guid.Parse((string) reObject[childName]));
                                    sharedObject.GetType()
                                        .GetProperty("SharedId", BindingFlags.NonPublic | BindingFlags.Instance)
                                        .SetValue(sharedObject, Guid.Parse(reObject["id"].ToString()));

                                    list.Add(sharedObject);
                                }

                                var array = Array.CreateInstance(p.PropertyType.GetElementType(), list.Count);
                                list.CopyTo(array, 0);

                                p.SetValue(resultingDocument, array);
                            }
                        }
                        else
                        {
                            if (p.PropertyType.Name == "RethinkArray`1")
                            {
                                var listType =
                                    typeof(RethinkArray<>).MakeGenericType(p.PropertyType.GenericTypeArguments[0]);
                                var list = Activator.CreateInstance(listType);


                                var method = list.GetType().GetMethod("AddRethinkItem",
                                    BindingFlags.NonPublic | BindingFlags.Instance);

                                var jArray = (JArray) jsonObject[p.Name + "_list"];

                                foreach (var t in jArray)
                                {
                                    method.Invoke(list, new object[]
                                    {
                                        new RethinkItem()
                                        {
                                            Id = Guid.Parse(t.ToString()),
                                            Table = childTypeName
                                        }
                                    });
                                }

                                p.SetValue(resultingDocument, list);
                            }
                            else
                            {
                                var jArray = (JArray) jsonObject[p.Name + "_list"];
                                var newArray = Array.CreateInstance(p.PropertyType.GetElementType(), jArray.Count);
                                for (var index = 0; index < jArray.Count; index++)
                                {
                                    newArray.SetValue(
                                        FindOne(p.PropertyType.GetElementType(), Guid.Parse(jArray[index].ToString())),
                                        index);
                                }

                                p.SetValue(resultingDocument, newArray);
                            }
                        }
                    }
                    else
                    {
                        p.SetValue(resultingDocument,
                            FindOne(p.PropertyType, Guid.Parse((string) jsonObject[p.Name + "_id"])));
                    }
                }
                else
                {
                    if (p.Name == "Id")
                    {
                        p.SetValue(resultingDocument, Guid.Parse((string) jsonObject["id"]));
                    }
                    else
                    {
                        switch (p.PropertyType.Name)
                        {
                            case nameof(Guid):
                                p.SetValue(resultingDocument, Guid.Parse((string) jsonObject[p.Name]));
                                break;
                            case nameof(DateTime):
                                p.SetValue(resultingDocument, DateTime.Parse((string) jsonObject[p.Name]));
                                break;
                            default:
                                p.SetValue(resultingDocument, jsonObject[p.Name]);
                                break;
                        }
                    }
                }
            }

            return resultingDocument;
        }

        /// <summary>
        /// This method is primarily used for RethinkArray items to support delayed loading of items
        /// </summary>
        /// <param name="id"></param>
        /// <param name="overideTableName"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        internal static T GetObject<T>(Guid id, string overideTableName = null)
        {
            return (T) GetObject(typeof(T), id, overideTableName);
        }
    }
}