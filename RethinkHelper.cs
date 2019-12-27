using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RethinkDb.Driver;
using RethinkDb.Driver.Ast;
using RethinkDb.Driver.Extras.Dao;
using RethinkDb.Driver.Model;
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
        public static T Dispense<T>() where T : RethinkObject<T, Guid>, IDocument<Guid>, new()
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
            var tableName = type.Name;
            var targetType = type;
            if (type.IsArray)
            {
                tableName = tableName.Replace("[]", "");
                targetType = targetType.GetElementType();
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
                // Get and set methods have to be public
                if (mget == null) continue;
                if (mset == null) continue;

                if (Attribute.IsDefined(p, typeof(RefTableAttribute)))
                {
                    if (p.PropertyType.IsArray)
                    {
                        var jArray = (JArray) jsonObject[p.Name + "_list"];
                        var newArray = Array.CreateInstance(p.PropertyType.GetElementType(), jArray.Count);
                        for (var index = 0; index < jArray.Count; index++)
                        {
                            newArray.SetValue(
                                FindOne(p.PropertyType.GetElementType(), Guid.Parse(jArray[index].ToString())), index);
                        }

                        p.SetValue(resultingDocument, newArray);
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
            var jsonObject = GetAndStoreJObject(rethinkObject);
            var tableName = rethinkObject.GetType().Name;

            if (!Frozen && !RethinkDB.R.TableList().Contains(tableName).RunAtom<bool>(ConnectionPool))
            {
                RethinkDB.R.TableCreate(tableName).Run(ConnectionPool);
                foreach (var property in rethinkObject.GetType().GetProperties())
                {
                    if (Attribute.IsDefined(property, typeof(SecondaryIndexAttribute)))
                    {
                        RethinkDB.R.Table(tableName).IndexCreate(property.Name).RunNoReply(ConnectionPool);
                        RethinkDB.R.Table(tableName).IndexWait().Run(ConnectionPool);
                    }

                    if (Attribute.IsDefined(property, typeof(RefTableAttribute)))
                    {
                        //TODO: Shared Array list tables?
                        if (!property.PropertyType.IsArray)
                        {
                            RethinkDB.R.Table(tableName).IndexCreate(property.Name + "_id").RunNoReply(ConnectionPool);
                        }

                        RethinkDB.R.Table(tableName).IndexWait().Run(ConnectionPool);
                    }
                }
            }

            var result = await RethinkDB.R.Table(tableName)
                .Insert(jsonObject)[new {return_changes = true}].OptArg("conflict", "replace")
                .RunWriteAsync(ConnectionPool)
                .ConfigureAwait(false);

            result.AssertNoErrors();

            if (result.Inserted == 0) return (Guid) jsonObject["id"];

            return result.GeneratedKeys[0];
        }

        public static void Trash(object rethinkObject, bool recursive = false)
        {
            TrashAsync(rethinkObject, recursive).WaitSync();
        }

        public static async Task TrashAsync(object rethinkObject, bool recursive = false)
        {
            var properties = rethinkObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

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

                if (Attribute.IsDefined(p, typeof(RefTableAttribute)))
                {
                    if (recursive) //TODO: Implement shared and Private lists, shared are kept in event of deletion
                    {
                        if (p.PropertyType.IsArray)
                        {
                            foreach (var reObject in (Array) p.GetValue(rethinkObject))
                            {
                                await TrashAsync(reObject, true);
                            }
                        }
                        else
                        {
                            //Recursively store all children items loaded in the object
                            await TrashAsync(p.GetValue(rethinkObject), true);
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

        public static void Trash<T>(RethinkObject<T, Guid> obj, bool recursive = false) where T : IDocument<Guid>, new()
        {
            TrashAsync(obj, recursive).WaitSync();
        }

        public static async Task TrashAsync<T>(RethinkObject<T, Guid> obj, bool recursive = false)
            where T : IDocument<Guid>, new()
        {
            await TrashAsync((object) obj, recursive);
        }

        public static JObject GetAndStoreJObject(object rethinkObject)
        {
            var obj = new JObject();
            var properties = rethinkObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

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

                if (Attribute.IsDefined(p, typeof(RefTableAttribute)))
                {
                    if (p.PropertyType.IsArray)
                    {
                        obj[p.Name + "_list"] = new JArray((from object reObject in (Array) p.GetValue(rethinkObject)
                            select Store(reObject)));
                    }
                    else
                    {
                        //Recursively store all children items loaded in the object
                        obj[p.Name + "_id"] = Store(p.GetValue(rethinkObject));
                    }
                }
                else
                {
                    if (p.Name == "Id")
                    {
                        //Only assign the id if we actually have one. Let RethinkDB generate it.
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

            return obj;
        }
    }
}