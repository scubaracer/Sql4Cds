﻿using MarkMpn.Sql4Cds.Engine.FetchXml;
using MarkMpn.Sql4Cds.Engine.QueryExtensions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace MarkMpn.Sql4Cds.Engine
{
    /// <summary>
    /// A CDS query that can be executed
    /// </summary>
    /// <remarks>
    /// Further details for specific query types can be found from one of the derived types, but all query types can be executed
    /// using the same <see cref="Execute(IOrganizationService, IAttributeMetadataCache, IQueryExecutionOptions)"/> method.
    /// </remarks>
    public abstract class Query
    {
        /// <summary>
        /// The original SQL that the query was created from
        /// </summary>
        public string Sql { get; set; }

        /// <summary>
        /// Executes the query
        /// </summary>
        /// <param name="org">The <see cref="IOrganizationService"/> to execute the query against</param>
        /// <param name="metadata">The metadata cache to use when executing the query</param>
        /// <param name="options">The options to apply to the query execution</param>
        /// <remarks>
        /// After calling this method, the results can be retrieved from the <see cref="Result"/> property.
        /// </remarks>
        public void Execute(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            try
            {
                Result = ExecuteInternal(org, metadata, options);
            }
            catch (Exception ex)
            {
                Result = ex;
            }
        }

        /// <summary>
        /// Performs the actual query execution. Any exception thrown here will be captured in the <see cref="Result"/> property.
        /// </summary>
        /// <param name="org">The <see cref="IOrganizationService"/> to execute the query against</param>
        /// <param name="metadata">The metadata cache to use when executing the query</param>
        /// <param name="options">The options to apply to the query execution</param>
        protected abstract object ExecuteInternal(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options);

        /// <summary>
        /// The results of the query after <see cref="Execute(IOrganizationService, IAttributeMetadataCache, IQueryExecutionOptions)"/>
        /// has been called.
        /// </summary>
        /// <remarks>
        /// Depending on the type of query, this property can return a <see cref="EntityCollection"/> (for a <see cref="SelectQuery"/>),
        /// an exception if the query failed, or a <see cref="String"/> value containing a human-readable description of the query status.
        /// </remarks>
        public object Result { get; private set; }
    }

    /// <summary>
    /// A CDS query that is based on FetchXML
    /// </summary>
    public abstract class FetchXmlQuery : Query
    {
        private FetchType _fetch;

        /// <summary>
        /// The FetchXML query
        /// </summary>
        public FetchType FetchXml
        {
            get { return _fetch; }
            set
            {
                _fetch = value;
                FetchXmlString = Serialize(_fetch);
            }
        }

        /// <summary>
        /// The string representation of the <see cref="FetchXml"/>
        /// </summary>
        public string FetchXmlString { get; private set; }

        /// <summary>
        /// Indicates if the query will page across all the available data
        /// </summary>
        public bool AllPages { get; set; }

        /// <summary>
        /// A list of any post-processing functions to be applied to the results
        /// </summary>
        public IList<IQueryExtension> Extensions { get; } = new List<IQueryExtension>();

        /// <summary>
        /// Returns an alternative query that can be used if the main aggregate query results in an AggregateQueryRecordLimit error
        /// </summary>
        public FetchXmlQuery AggregateAlternative { get; set; }

        /// <summary>
        /// Retrieves all the data matched by the <see cref="FetchXml"/>
        /// </summary>
        /// <param name="org">The <see cref="IOrganizationService"/> to execute the query against</param>
        /// <param name="metadata">The metadata cache to use when executing the query</param>
        /// <param name="options">The options to apply to the query execution</param>
        /// <returns>The records matched by the query</returns>
        protected EntityCollection RetrieveAll(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            if (options.Cancelled)
                return null;

            try
            {
                var res = new EntityCollection(RetrieveSequence(org, metadata, options).ToList());
                res.EntityName = FetchXml.Items.OfType<FetchEntityType>().Single().name;

                return res;
            }
            catch (Exception ex)
            {
                // Attempt to handle aggregate queries that go over the standard FetchXML limit by rewriting them to retrieve the
                // individual records and apply the aggregation in-memory
                if (!ex.Message.Contains("AggregateQueryRecordLimit"))
                    throw;

                if (AggregateAlternative == null)
                    throw;

                return AggregateAlternative.RetrieveAll(org, metadata, options);
            }
        }

        /// <summary>
        /// Retrieves all the data matched by the <see cref="FetchXml"/>
        /// </summary>
        /// <param name="org">The <see cref="IOrganizationService"/> to execute the query against</param>
        /// <param name="metadata">The metadata cache to use when executing the query</param>
        /// <param name="options">The options to apply to the query execution</param>
        /// <returns>The records matched by the query, with any custom filters, calculated fields and sorted applied</returns>
        protected IEnumerable<Entity> RetrieveSequence(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            var sequence = RetrieveSequenceInternal(org, metadata, options);

            foreach (var extension in Extensions)
                sequence = extension.ApplyTo(sequence, options);

            return sequence;
        }

        /// <summary>
        /// Retrieves all the data matched by the <see cref="FetchXml"/>
        /// </summary>
        /// <param name="org">The <see cref="IOrganizationService"/> to execute the query against</param>
        /// <param name="metadata">The metadata cache to use when executing the query</param>
        /// <param name="options">The options to apply to the query execution</param>
        /// <returns>The records matched by the query, with any custom filters and calculated fields applied</returns>
        private IEnumerable<Entity> RetrieveSequenceInternal(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            if (options.Cancelled)
                yield break;

            var mainEntity = FetchXml.Items.OfType<FetchEntityType>().Single();
            var name = mainEntity.name;
            var meta = metadata[name];
            options.Progress($"Retrieving {meta.DisplayCollectionName?.UserLocalizedLabel?.Label}...");

            // Get the first page of results
            var res = org.RetrieveMultiple(new FetchExpression(Serialize(FetchXml)));

            foreach (var entity in res.Entities)
                yield return entity;

            var count = res.Entities.Count;

            // Aggregate queries return up to 5000 records and don't provide a method to move on to the next page
            // Throw an exception to indicate the error to the caller
            if (AllPages && FetchXml.aggregateSpecified && FetchXml.aggregate && count == 5000 && FetchXml.top != "5000" && !res.MoreRecords)
                throw new ApplicationException("AggregateQueryRecordLimit");

            // Move on to subsequent pages
            while (AllPages && res.MoreRecords && !options.Cancelled && options.ContinueRetrieve(count))
            {
                options.Progress($"Retrieved {count:N0} {meta.DisplayCollectionName?.UserLocalizedLabel?.Label}...");

                if (FetchXml.page == null)
                    FetchXml.page = "2";
                else
                    FetchXml.page = (Int32.Parse(FetchXml.page) + 1).ToString();

                FetchXml.pagingcookie = res.PagingCookie;

                var nextPage = org.RetrieveMultiple(new FetchExpression(Serialize(FetchXml)));

                foreach (var entity in nextPage.Entities)
                    yield return entity;

                count += nextPage.Entities.Count;
                res = nextPage;
            }
        }

        /// <summary>
        /// Convert the FetchXML query object to a string
        /// </summary>
        /// <param name="fetch">The FetchXML query object to convert</param>
        /// <returns>The string representation of the query</returns>
        internal static string Serialize(FetchType fetch)
        {
            var serializer = new XmlSerializer(typeof(FetchType));

            using (var writer = new StringWriter())
            using (var xmlWriter = System.Xml.XmlWriter.Create(writer, new System.Xml.XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Indent = true
            }))
            {
                // Add in a separate namespace to remove the xsi and xsd namespaces added by default
                var xsn = new XmlSerializerNamespaces();
                xsn.Add("generator", "MarkMpn.SQL4CDS");

                serializer.Serialize(xmlWriter, fetch, xsn);
                return writer.ToString();
            }
        }
    }

    /// <summary>
    /// A SELECT query to return data using FetchXML
    /// </summary>
    public class SelectQuery : FetchXmlQuery
    {
        /// <inheritdoc/>
        protected override object ExecuteInternal(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            // Shortcut getting the total number of records in an entity where possible
            if (RetrieveTotalRecordCount(org, metadata, out var result))
                return result;

            // Run the raw SQL query against the T-SQL endpoint
            if (ExecuteTSQL(org, options, out var dataTable))
                return dataTable;

            // Execute the FetchXML
            return RetrieveAll(org, metadata, options);
        }

        /// <summary>
        /// Run the raw SQL query against the T-SQL endpoint
        /// </summary>
        /// <param name="org">The <see cref="IOrganizationService"/> to execute the query against</param>
        /// <param name="options">The options that indicate if the T-SQL endpoint should be used</param>
        /// <param name="result">The results of running the query</param>
        /// <returns><c>true</c> if this method has executed the query, or <c>false otherwise</c></returns>
        private bool ExecuteTSQL(IOrganizationService org, IQueryExecutionOptions options, out DataTable result)
        {
            result = null;

            if (String.IsNullOrEmpty(Sql))
                return false;

            if (!options.UseTSQLEndpoint)
                return false;

            if (!(org is CrmServiceClient svc))
                return false;

            if (String.IsNullOrEmpty(svc.CurrentAccessToken))
                return false;

            if (!TSqlEndpoint.IsEnabled(svc))
                return false;

            using (var con = new SqlConnection("server=" + svc.CrmConnectOrgUriActual.Host + ",5558"))
            {
                con.AccessToken = svc.CurrentAccessToken;
                con.Open();

                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = Sql;
                    result = new DataTable();

                    using (var adapter = new SqlDataAdapter(cmd))
                    {
                        adapter.Fill(result);
                    }

                    return true;
                }
            }
        }

        /// <summary>
        /// Get the total number of records in an entity
        /// </summary>
        /// <param name="org">The <see cref="IOrganizationService"/> to execute the query against</param>
        /// <param name="metadata">The metadata cache to use when executing the query</param>
        /// <returns><c>true</c> if this method has retrieved the requested details, or <c>false</c> otherwise</returns>
        private bool RetrieveTotalRecordCount(IOrganizationService org, IAttributeMetadataCache metadata, out EntityCollection result)
        {
            result = null;

            if (FetchXml == null)
                return false;

            if (Extensions.Count > 0)
                return false;

            // Special case - SELECT count(primaryid) with no filter
            if (!FetchXml.aggregate)
                return false;
            
            var entity = FetchXml.Items.OfType<FetchEntityType>().Single();
            var attributes = entity.Items.OfType<FetchAttributeType>().ToArray();

            if (attributes.Length != 1 || attributes[0].aggregate != AggregateType.count)
                return false;

            var filters = entity.Items.OfType<filter>().Count();
            var links = entity.Items.OfType<FetchLinkEntityType>().Count();

            if (filters != 0 || links != 0)
                return false;

            if (attributes[0].name != metadata[entity.name].PrimaryIdAttribute)
                return false;

            // RetrieveTotalRecordCountRequest is only supported in v9+
            var version = (RetrieveVersionResponse)org.Execute(new RetrieveVersionRequest());

            if (!Version.TryParse(version.Version, out var serverVersion) || serverVersion.Major < 9)
                return false;

            var count = ((RetrieveTotalRecordCountResponse)org.Execute(new RetrieveTotalRecordCountRequest { EntityNames = new[] { entity.name } })).EntityRecordCountCollection[entity.name];

            var resultEntity = new Entity(entity.name)
            {
                [attributes[0].alias] = new AliasedValue(entity.name, attributes[0].name, count)
            };

            result = new EntityCollection { EntityName = entity.name, Entities = { resultEntity } };
            return true;
        }

        /// <summary>
        /// The columns that should be included in the query results
        /// </summary>
        public string[] ColumnSet { get; set; }
    }

    /// <summary>
    /// An UPDATE query to identify records via FetchXML and change them
    /// </summary>
    public class UpdateQuery : FetchXmlQuery
    {
        /// <summary>
        /// The logical name of the entity to update
        /// </summary>
        public string EntityName { get; set; }

        /// <summary>
        /// The primary key attribute in the entity to update
        /// </summary>
        public string IdColumn { get; set; }

        /// <summary>
        /// A mapping of attribute names to values to apply updates to
        /// </summary>
        public IDictionary<string,Func<Entity,object>> Updates { get; set; }

        /// <inheritdoc/>
        protected override object ExecuteInternal(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            if (options.Cancelled)
                return null;

            // Check if the update is allowed
            if (options.BlockUpdateWithoutWhere && !FetchXml.Items.OfType<FetchEntityType>().Single().Items.OfType<filter>().Any())
                throw new InvalidOperationException("UPDATE without WHERE is blocked by your settings");

            // Get the records to update
            var count = 0;
            var entities = RetrieveAll(org, metadata, options).Entities;

            if (entities == null)
                return null;

            var meta = metadata[EntityName];

            // Check again that the update is allowed
            if (!options.ConfirmUpdate(entities.Count, meta))
                throw new OperationCanceledException("UPDATE cancelled by user");

            // Apply the update in batches
            ExecuteMultipleRequest multiple = null;

            foreach (var entity in entities)
            {
                if (options.Cancelled)
                    break;

                var id = entity[IdColumn];
                if (id is AliasedValue alias)
                    id = alias.Value;

                var update = new Entity(EntityName);
                update.Id = (Guid)id;

                foreach (var attr in Updates)
                    update[attr.Key] = attr.Value(entity);

                if (options.BatchSize == 1)
                {
                    options.Progress($"Updating {meta.DisplayName?.UserLocalizedLabel?.Label} {count + 1:N0} of {entities.Count:N0}...");
                    org.Update(update);
                    count++;
                }
                else
                {
                    if (multiple == null)
                    {
                        multiple = new ExecuteMultipleRequest
                        {
                            Requests = new OrganizationRequestCollection(),
                            Settings = new ExecuteMultipleSettings
                            {
                                ContinueOnError = false,
                                ReturnResponses = false
                            }
                        };
                    }

                    multiple.Requests.Add(new UpdateRequest { Target = update });

                    if (multiple.Requests.Count == options.BatchSize)
                    {
                        options.Progress($"Updating {meta.DisplayCollectionName?.UserLocalizedLabel?.Label} {count + 1:N0} - {count + multiple.Requests.Count:N0} of {entities.Count:N0}...");
                        var resp = (ExecuteMultipleResponse) org.Execute(multiple);
                        if (resp.IsFaulted)
                            throw new ApplicationException($"Error updating {meta.DisplayCollectionName?.UserLocalizedLabel?.Label}");

                        count += multiple.Requests.Count;

                        multiple = null;
                    }
                }
            }

            if (!options.Cancelled && multiple != null)
            {
                options.Progress($"Updating {meta.DisplayCollectionName?.UserLocalizedLabel?.Label} {count + 1:N0} - {count + multiple.Requests.Count:N0} of {entities.Count:N0}...");
                var resp = (ExecuteMultipleResponse)org.Execute(multiple);
                if (resp.IsFaulted)
                    throw new ApplicationException($"Error updating {meta.DisplayCollectionName?.UserLocalizedLabel?.Label}");

                count += multiple.Requests.Count;
            }

            return $"{count:N0} {meta.DisplayCollectionName?.UserLocalizedLabel?.Label} updated";
        }
    }

    /// <summary>
    /// A DELETE query to identify records via FetchXML and delete them
    /// </summary>
    public class DeleteQuery : FetchXmlQuery
    {
        /// <summary>
        /// The logical name of the entity to delete
        /// </summary>
        public string EntityName { get; set; }

        /// <summary>
        /// The primary key columns of the entity to delete
        /// </summary>
        public string[] IdColumns { get; set; }

        /// <inheritdoc/>
        protected override object ExecuteInternal(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            // Check if the query is allowed
            if (options.Cancelled)
                return null;

            if (options.BlockDeleteWithoutWhere && !FetchXml.Items.OfType<FetchEntityType>().Single().Items.OfType<filter>().Any())
                throw new InvalidOperationException("DELETE without WHERE is blocked by your settings");

            var meta = metadata[EntityName];

            // If we are using a bulk delete job, start the job
            if (options.UseBulkDelete && Extensions.Count == 0 && meta.IsIntersect != true)
            {
                var query = ((FetchXmlToQueryExpressionResponse)org.Execute(new FetchXmlToQueryExpressionRequest { FetchXml = Serialize(FetchXml) })).Query;

                var bulkDelete = new BulkDeleteRequest
                {
                    JobName = $"SQL 4 CDS {meta.DisplayCollectionName.UserLocalizedLabel.Label} Bulk Delete Job",
                    QuerySet = new[] { query },
                    StartDateTime = DateTime.Now,
                    RunNow = true,
                    RecurrencePattern = String.Empty,
                    SendEmailNotification = false,
                    ToRecipients = new Guid[0],
                    CCRecipients = new Guid[0]
                };

                org.Execute(bulkDelete);

                return "Bulk delete job started";
            }

            // Otherwise, get the records to delete
            var count = 0;
            var entities = RetrieveAll(org, metadata, options).Entities;

            if (entities == null)
                return null;

            // Check again if the query is allowed
            if (!options.ConfirmDelete(entities.Count, meta))
                throw new OperationCanceledException("DELETE cancelled by user");

            ExecuteMultipleRequest multiple = null;

            // Delete hte records in batches
            foreach (var entity in entities)
            {
                if (options.Cancelled)
                    break;

                if (options.BatchSize == 1)
                {
                    options.Progress($"Deleting {meta.DisplayName.UserLocalizedLabel.Label} {count + 1:N0} of {entities.Count:N0}...");
                    org.Execute(CreateDeleteRequest(meta, entity));
                    count++;
                }
                else
                {
                    if (multiple == null)
                    {
                        multiple = new ExecuteMultipleRequest
                        {
                            Requests = new OrganizationRequestCollection(),
                            Settings = new ExecuteMultipleSettings
                            {
                                ContinueOnError = false,
                                ReturnResponses = false
                            }
                        };
                    }

                    multiple.Requests.Add(CreateDeleteRequest(meta, entity));

                    if (multiple.Requests.Count == options.BatchSize)
                    {
                        options.Progress($"Deleting {meta.DisplayCollectionName.UserLocalizedLabel.Label} {count + 1:N0} - {count + multiple.Requests.Count:N0} of {entities.Count:N0}...");
                        var resp = (ExecuteMultipleResponse)org.Execute(multiple);
                        if (resp.IsFaulted)
                            throw new ApplicationException($"Error deleting {meta.DisplayCollectionName.UserLocalizedLabel.Label}");

                        count += multiple.Requests.Count;

                        multiple = null;
                    }
                }
            }

            if (!options.Cancelled && multiple != null)
            {
                options.Progress($"Deleting {meta.DisplayCollectionName.UserLocalizedLabel.Label} {count + 1:N0} - {count + multiple.Requests.Count:N0}...");
                var resp = (ExecuteMultipleResponse)org.Execute(multiple);
                if (resp.IsFaulted)
                    throw new ApplicationException($"Error deleting {meta.DisplayCollectionName.UserLocalizedLabel.Label}");

                count += multiple.Requests.Count;
            }

            return $"{count:N0} {meta.DisplayCollectionName.UserLocalizedLabel.Label} deleted";
        }

        private OrganizationRequest CreateDeleteRequest(EntityMetadata meta, Entity entity)
        {
            // Special case messages for intersect entities
            if (meta.LogicalName == "listmember")
            {
                var listId = entity.GetAliasedAttributeValue<EntityReference>(IdColumns[0]);
                var entityId = entity.GetAliasedAttributeValue<EntityReference>(IdColumns[1]);

                return new RemoveMemberListRequest { ListId = listId.Id, EntityId = entityId.Id };
            }
            else if (meta.IsIntersect == true)
            {
                var entity1Id = entity.GetAliasedAttributeValue<EntityReference>(IdColumns[0]);
                var entity2Id = entity.GetAliasedAttributeValue<EntityReference>(IdColumns[1]);
                var relationship = meta.ManyToManyRelationships.Single();

                return new DisassociateRequest
                {
                    Target = entity1Id,
                    RelatedEntities = new EntityReferenceCollection(new[] { entity2Id }),
                    Relationship = new Relationship(relationship.SchemaName) { PrimaryEntityRole = EntityRole.Referencing }
                };
            }
            else
            {
                var id = entity.GetAliasedAttributeValue<Guid>(IdColumns[0]);

                return new DeleteRequest { Target = new EntityReference(EntityName, id) };
            }
        }
    }

    public abstract class InsertQuery : Query
    {
        /// <summary>
        /// The logical name of the entity to add
        /// </summary>
        public string LogicalName { get; set; }

        /// <summary>
        /// Returns a sequence of the entities to insert
        /// </summary>
        /// <returns></returns>
        protected abstract Entity[] GetValues(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options);

        protected override object ExecuteInternal(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            var meta = metadata[LogicalName];

            // Add each record in turn
            var count = 0;
            var entities = GetValues(org, metadata, options);

            if (entities != null)
            {
                foreach (var entity in entities)
                {
                    if (options.Cancelled)
                        break;

                    // Special cases for intersect entities
                    if (LogicalName == "listmember")
                    {
                        var listId = entity.GetAttributeValue<EntityReference>("listid");
                        var entityId = entity.GetAttributeValue<EntityReference>("entityid");

                        if (listId == null)
                            throw new ApplicationException("listid is required");

                        if (entityId == null)
                            throw new ApplicationException("entityid is required");

                        org.Execute(new AddMemberListRequest
                        {
                            ListId = listId.Id,
                            EntityId = entityId.Id
                        });
                    }
                    else if (meta.IsIntersect == true)
                    {
                        // For generic intersect entities we expect a single many-to-many relationship in the metadata which describes
                        // the relationship that this is the intersect entity for
                        var relationship = meta.ManyToManyRelationships.Single();

                        var entity1 = entity.GetAttributeValue<EntityReference>(relationship.Entity1IntersectAttribute);
                        var entity2 = entity.GetAttributeValue<EntityReference>(relationship.Entity2IntersectAttribute);

                        if (entity1 == null)
                            throw new ApplicationException($"{relationship.Entity1IntersectAttribute} is required");

                        if (entity2 == null)
                            throw new ApplicationException($"{relationship.Entity2IntersectAttribute} is required");

                        org.Execute(new AssociateRequest
                        {
                            Target = entity1,
                            Relationship = new Relationship(relationship.SchemaName) { PrimaryEntityRole = EntityRole.Referencing },
                            RelatedEntities = new EntityReferenceCollection(new[] { entity2 })
                        });
                    }
                    else
                    {
                        org.Create(entity);
                    }

                    count++;

                    options.Progress($"Inserted {count:N0} of {entities.Length:N0} {meta.DisplayCollectionName.UserLocalizedLabel.Label} ({(float)count / entities.Length:P0})");
                }
            }

            return $"{entities.Length:N0} {meta.DisplayCollectionName.UserLocalizedLabel.Label} inserted";
        }
    }

    /// <summary>
    /// An INSERT query to add fixed values
    /// </summary>
    public class InsertValues : InsertQuery
    {
        /// <summary>
        /// A list of records to insert
        /// </summary>
        public IDictionary<string, object>[] Values { get; set; }

        protected override Entity[] GetValues(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            return Values
                .Select(dictionary =>
                {
                    var entity = new Entity(LogicalName);

                    foreach (var attr in dictionary)
                        entity[attr.Key] = attr.Value;

                    return entity;
                })
                .ToArray();
        }
    }

    /// <summary>
    /// An INSERT query to add records based on the results of a FetchXML query
    /// </summary>
    public class InsertSelect : InsertQuery
    {
        /// <summary>
        /// The SELECT query that produces the values to insert
        /// </summary>
        public SelectQuery Source { get; set; }

        /// <summary>
        /// The mappings of columns from the FetchXML results to the attributes of the entity to insert
        /// </summary>
        public IDictionary<string, string> Mappings { get; set; }

        protected override Entity[] GetValues(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            Source.Execute(org, metadata, options);

            if (Source.Result is Exception ex)
                throw ex;

            if (!(Source.Result is EntityCollection entities))
                return null;

            var converted = new List<Entity>(entities.Entities.Count);

            foreach (var entity in entities.Entities)
            {
                if (options.Cancelled)
                    break;

                var newEntity = new Entity(LogicalName);

                foreach (var attr in Mappings)
                {
                    object value = null;

                    if (entity.Contains(attr.Key))
                        value = entity[attr.Key];

                    if (value is Guid g)
                        value = new EntityReference(entity.LogicalName, g);

                    newEntity[attr.Value] = value;
                }

                converted.Add(newEntity);
            }

            return converted.ToArray();
        }
    }
}
