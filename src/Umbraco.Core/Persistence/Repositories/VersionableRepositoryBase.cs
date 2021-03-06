﻿using System;
using System.Collections.Generic;
using System.Linq;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Editors;
using Umbraco.Core.Models.EntityBase;
using Umbraco.Core.Models.Rdbms;
using Umbraco.Core.Persistence.Caching;
using Umbraco.Core.Persistence.Factories;
using Umbraco.Core.Persistence.UnitOfWork;
using Umbraco.Core.PropertyEditors;
using Umbraco.Core.Services;

namespace Umbraco.Core.Persistence.Repositories
{
    internal abstract class VersionableRepositoryBase<TId, TEntity> : PetaPocoRepositoryBase<TId, TEntity>
        where TEntity : class, IAggregateRoot
    {
        protected VersionableRepositoryBase(IDatabaseUnitOfWork work) : base(work)
        {
        }

        protected VersionableRepositoryBase(IDatabaseUnitOfWork work, IRepositoryCacheProvider cache) : base(work, cache)
        {
        }

        #region IRepositoryVersionable Implementation

        public virtual IEnumerable<TEntity> GetAllVersions(int id)
        {
            var sql = new Sql();
            sql.Select("*")
                .From<ContentVersionDto>()
                .InnerJoin<ContentDto>()
                .On<ContentVersionDto, ContentDto>(left => left.NodeId, right => right.NodeId)
                .InnerJoin<NodeDto>()
                .On<ContentDto, NodeDto>(left => left.NodeId, right => right.NodeId)
                .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId)
                .Where<NodeDto>(x => x.NodeId == id)
                .OrderByDescending<ContentVersionDto>(x => x.VersionDate);

            var dtos = Database.Fetch<ContentVersionDto, ContentDto, NodeDto>(sql);
            foreach (var dto in dtos)
            {
                yield return GetByVersion(dto.VersionId);
            }
        }

        public virtual void DeleteVersion(Guid versionId)
        {
            var dto = Database.FirstOrDefault<ContentVersionDto>("WHERE versionId = @VersionId", new { VersionId = versionId });
            if(dto == null) return;

            //Ensure that the lastest version is not deleted
            var latestVersionDto = Database.FirstOrDefault<ContentVersionDto>("WHERE ContentId = @Id ORDER BY VersionDate DESC", new { Id = dto.NodeId });
            if(latestVersionDto.VersionId == dto.VersionId)
                return;

            using (var transaction = Database.GetTransaction())
            {
                PerformDeleteVersion(dto.NodeId, versionId);

                transaction.Complete();
            }
        }

        public virtual void DeleteVersions(int id, DateTime versionDate)
        {
            //Ensure that the latest version is not part of the versions being deleted
            var latestVersionDto = Database.FirstOrDefault<ContentVersionDto>("WHERE ContentId = @Id ORDER BY VersionDate DESC", new { Id = id });
            var list =
                Database.Fetch<ContentVersionDto>(
                    "WHERE versionId <> @VersionId AND (ContentId = @Id AND VersionDate < @VersionDate)",
                    new {VersionId = latestVersionDto.VersionId, Id = id, VersionDate = versionDate});
            if (list.Any() == false) return;

            using (var transaction = Database.GetTransaction())
            {
                foreach (var dto in list)
                {
                    PerformDeleteVersion(id, dto.VersionId);
                }

                transaction.Complete();
            }
        }

        public abstract TEntity GetByVersion(Guid versionId);

        /// <summary>
        /// Protected method to execute the delete statements for removing a single version for a TEntity item.
        /// </summary>
        /// <param name="id">Id of the <see cref="TEntity"/> to delete a version from</param>
        /// <param name="versionId">Guid id of the version to delete</param>
        protected abstract void PerformDeleteVersion(int id, Guid versionId);

        #endregion

        public int CountDescendants(int parentId, string contentTypeAlias = null)
        {
            var pathMatch = parentId == -1
                ? "-1,"
                : "," + parentId + ",";
            var sql = new Sql();
            if (contentTypeAlias.IsNullOrWhiteSpace())
            {
                sql.Select("COUNT(*)")
                    .From<NodeDto>()
                    .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId)
                    .Where<NodeDto>(x => x.Path.Contains(pathMatch));
            }
            else
            {
                sql.Select("COUNT(*)")
                    .From<NodeDto>()
                    .InnerJoin<ContentDto>()
                    .On<NodeDto, ContentDto>(left => left.NodeId, right => right.NodeId)
                    .InnerJoin<ContentTypeDto>()
                    .On<ContentTypeDto, ContentDto>(left => left.NodeId, right => right.ContentTypeId)
                    .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId)
                    .Where<NodeDto>(x => x.Path.Contains(pathMatch))
                    .Where<ContentTypeDto>(x => x.Alias == contentTypeAlias);
            }

            return Database.ExecuteScalar<int>(sql);
        }

        public int CountChildren(int parentId, string contentTypeAlias = null)
        {
            var sql = new Sql();
            if (contentTypeAlias.IsNullOrWhiteSpace())
            {
                sql.Select("COUNT(*)")
                    .From<NodeDto>()
                    .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId)
                    .Where<NodeDto>(x => x.ParentId == parentId);
            }
            else
            {
                sql.Select("COUNT(*)")
                    .From<NodeDto>()
                    .InnerJoin<ContentDto>()
                    .On<NodeDto, ContentDto>(left => left.NodeId, right => right.NodeId)
                    .InnerJoin<ContentTypeDto>()
                    .On<ContentTypeDto, ContentDto>(left => left.NodeId, right => right.ContentTypeId)
                    .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId)
                    .Where<NodeDto>(x => x.ParentId == parentId)
                    .Where<ContentTypeDto>(x => x.Alias == contentTypeAlias);
            }

            return Database.ExecuteScalar<int>(sql);
        }

        /// <summary>
        /// Get the total count of entities
        /// </summary>
        /// <param name="contentTypeAlias"></param>
        /// <returns></returns>
        public int Count(string contentTypeAlias = null)
        {
            var sql = new Sql();
            if (contentTypeAlias.IsNullOrWhiteSpace())
            {
                sql.Select("COUNT(*)")
                    .From<NodeDto>()
                    .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId);
            }
            else
            {
                sql.Select("COUNT(*)")
                    .From<NodeDto>()
                    .InnerJoin<ContentDto>()
                    .On<NodeDto, ContentDto>(left => left.NodeId, right => right.NodeId)
                    .InnerJoin<ContentTypeDto>()
                    .On<ContentTypeDto, ContentDto>(left => left.NodeId, right => right.ContentTypeId)
                    .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId)
                    .Where<ContentTypeDto>(x => x.Alias == contentTypeAlias);
            }

            return Database.ExecuteScalar<int>(sql);
        }

        /// <summary>
        /// This removes associated tags from the entity - used generally when an entity is recycled
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="tagRepo"></param>
        protected void ClearEntityTags(IContentBase entity, ITagRepository tagRepo)
        {
            tagRepo.ClearTagsFromEntity(entity.Id);
        }

        /// <summary>
        /// Updates the tag repository with any tag enabled properties and their values
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="tagRepo"></param>
        protected void UpdatePropertyTags(IContentBase entity, ITagRepository tagRepo)
        {
            foreach (var tagProp in entity.Properties.Where(x => x.TagSupport.Enable))
            {
                if (tagProp.TagSupport.Behavior == PropertyTagBehavior.Remove)
                {
                    //remove the specific tags
                    tagRepo.RemoveTagsFromProperty(
                        entity.Id,
                        tagProp.PropertyTypeId,
                        tagProp.TagSupport.Tags.Select(x => new Tag { Text = x.Item1, Group = x.Item2 }));
                }
                else
                {
                    //assign the tags
                    tagRepo.AssignTagsToProperty(
                        entity.Id,
                        tagProp.PropertyTypeId,
                        tagProp.TagSupport.Tags.Select(x => new Tag { Text = x.Item1, Group = x.Item2 }),
                        tagProp.TagSupport.Behavior == PropertyTagBehavior.Replace);
                }
            }
        }
      
        protected IDictionary<int, PropertyCollection> GetPropertyCollection(
            Sql docSql,
            params DocumentDefinition[] documentDefs)
        {
            if (documentDefs.Length <= 0) return new Dictionary<int, PropertyCollection>();

            //we need to parse the original SQL statement and reduce the columns to just cmsContent.nodeId, cmsContentVersion.VersionId so that we can use 
            // the statement to go get the property data for all of the items by using an inner join
            var parsedOriginalSql = "SELECT {0} " + docSql.SQL.Substring(docSql.SQL.IndexOf("FROM", StringComparison.Ordinal));
            //now remove everything from an Orderby clause and beyond
            if (parsedOriginalSql.InvariantContains("ORDER BY "))
            {
                parsedOriginalSql = parsedOriginalSql.Substring(0, parsedOriginalSql.IndexOf("ORDER BY ", System.StringComparison.Ordinal));
            }

            var propSql = new Sql(@"SELECT cmsPropertyData.*
FROM cmsPropertyData
INNER JOIN cmsPropertyType
ON cmsPropertyData.propertytypeid = cmsPropertyType.id
INNER JOIN 
	(" + string.Format(parsedOriginalSql, "cmsContent.nodeId, cmsContentVersion.VersionId") + @") as docData
ON cmsPropertyData.versionId = docData.VersionId AND cmsPropertyData.contentNodeId = docData.nodeId
LEFT OUTER JOIN cmsDataTypePreValues
ON cmsPropertyType.dataTypeId = cmsDataTypePreValues.datatypeNodeId", docSql.Arguments);

            var allPropertyData = Database.Fetch<PropertyDataDto>(propSql);

            //This is a lazy access call to get all prevalue data for the data types that make up all of these properties which we use
            // below if any property requires tag support
            var allPreValues = new Lazy<IEnumerable<DataTypePreValueDto>>(() =>
            {
                var preValsSql = new Sql(@"SELECT DISTINCT
cmsDataTypePreValues.id as preValId, cmsDataTypePreValues.value, cmsDataTypePreValues.sortorder, cmsDataTypePreValues.alias, cmsDataTypePreValues.datatypeNodeId
FROM cmsDataTypePreValues
INNER JOIN cmsPropertyType
ON cmsDataTypePreValues.datatypeNodeId = cmsPropertyType.dataTypeId
INNER JOIN 
	(" + string.Format(parsedOriginalSql, "cmsContent.contentType") + @") as docData
ON cmsPropertyType.contentTypeId = docData.contentType", docSql.Arguments);

                return Database.Fetch<DataTypePreValueDto>(preValsSql);
            });

            var result = new Dictionary<int, PropertyCollection>();

            foreach (var def in documentDefs)
            {
                var propertyDataDtos = allPropertyData.Where(x => x.NodeId == def.Id).Distinct();

                var propertyFactory = new PropertyFactory(def.Composition, def.Version, def.Id, def.CreateDate, def.VersionDate);
                var properties = propertyFactory.BuildEntity(propertyDataDtos).ToArray();

                var newProperties = properties.Where(x => x.HasIdentity == false && x.PropertyType.HasIdentity);
                foreach (var property in newProperties)
                {
                    var propertyDataDto = new PropertyDataDto { NodeId = def.Id, PropertyTypeId = property.PropertyTypeId, VersionId = def.Version };
                    int primaryKey = Convert.ToInt32(Database.Insert(propertyDataDto));

                    property.Version = def.Version;
                    property.Id = primaryKey;
                }

                foreach (var property in properties)
                {
                    //NOTE: The benchmarks run with and without the following code show very little change so this is not a perf bottleneck
                    var editor = PropertyEditorResolver.Current.GetByAlias(property.PropertyType.PropertyEditorAlias);
                    var tagSupport = TagExtractor.GetAttribute(editor);

                    if (tagSupport != null)
                    {
                        //this property has tags, so we need to extract them and for that we need the prevals which we've already looked up
                        var preValData = allPreValues.Value.Where(x => x.DataTypeNodeId == property.PropertyType.DataTypeDefinitionId)
                            .Distinct()
                            .ToArray();
                            
                        var asDictionary = preValData.ToDictionary(x => x.Alias, x => new PreValue(x.Id, x.Value, x.SortOrder));

                        var preVals = new PreValueCollection(asDictionary);

                        var contentPropData = new ContentPropertyData(property.Value,
                            preVals,
                            new Dictionary<string, object>());

                        TagExtractor.SetPropertyTags(property, contentPropData, property.Value, tagSupport);
                    }
                }

                result.Add(def.Id, new PropertyCollection(properties));
            }

            return result;
        }

        public class DocumentDefinition
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="T:System.Object"/> class.
            /// </summary>
            public DocumentDefinition(int id, Guid version, DateTime versionDate, DateTime createDate, IContentTypeComposition composition)
            {
                Id = id;
                Version = version;
                VersionDate = versionDate;
                CreateDate = createDate;
                Composition = composition;
            }

            public int Id { get; set; }
            public Guid Version { get; set; }
            public DateTime VersionDate { get; set; }
            public DateTime CreateDate { get; set; }
            public IContentTypeComposition Composition { get; set; }
        }

        protected virtual string GetDatabaseFieldNameForOrderBy(string orderBy)
        {
            // Translate the passed order by field (which were originally defined for in-memory object sorting
            // of ContentItemBasic instances) to the database field names.
            switch (orderBy.ToUpperInvariant())
            {
                case "NAME":
                    return "cmsDocument.text";
                case "OWNER":
                    //TODO: This isn't going to work very nicely because it's going to order by ID, not by letter
                    return "umbracoNode.nodeUser";
                default:
                    return orderBy;
            }
        }

        protected virtual string GetEntityPropertyNameForOrderBy(string orderBy)
        {
            // Translate the passed order by field (which were originally defined for in-memory object sorting
            // of ContentItemBasic instances) to the IMedia property names.
            switch (orderBy.ToUpperInvariant())
            {
                case "OWNER":
                    //TODO: This isn't going to work very nicely because it's going to order by ID, not by letter
                    return "CreatorId";
                case "UPDATER":
                    //TODO: This isn't going to work very nicely because it's going to order by ID, not by letter
                    return "WriterId";
                default:
                    return orderBy;
            }
        }
    }
}