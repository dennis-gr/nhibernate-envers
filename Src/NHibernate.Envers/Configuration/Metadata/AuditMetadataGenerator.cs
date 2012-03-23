﻿using System.Collections.Generic;
using System.Linq;
using System.Xml;
using NHibernate.Envers.Configuration.Attributes;
using NHibernate.Envers.Configuration.Metadata.Reader;
using NHibernate.Envers.Entities;
using NHibernate.Envers.Entities.Mapper;
using NHibernate.Envers.Strategy;
using NHibernate.Envers.Tools;
using NHibernate.Mapping;
using NHibernate.Type;

namespace NHibernate.Envers.Configuration.Metadata
{
	public sealed class AuditMetadataGenerator
	{
		private static readonly IInternalLogger log = LoggerProvider.LoggerFor(typeof(AuditMetadataGenerator));

		public Cfg.Configuration Cfg { get; private set; }
		public GlobalConfiguration GlobalCfg { get; private set; }
		public AuditEntitiesConfiguration VerEntCfg { get; private set; }
		public IAuditStrategy AuditStrategy { get; private set; }
		private readonly XmlElement revisionInfoRelationMapping;

		/*
		 * Generators for different kinds of property values/types.
		 */
		public BasicMetadataGenerator BasicMetadataGenerator { get; private set; }
		private readonly ComponentMetadataGenerator componentMetadataGenerator;
		private readonly IdMetadataGenerator idMetadataGenerator;
		private readonly ToOneRelationMetadataGenerator toOneRelationMetadataGenerator;

		/*
		 * Here information about already generated mappings will be accumulated.
		 */
		public IDictionary<string, EntityConfiguration> EntitiesConfigurations { get; private set; }
		public IDictionary<string, EntityConfiguration> NotAuditedEntitiesConfigurations { get; private set; }
		public AuditEntityNameRegister AuditEntityNameRegister { get; private set; }

		// Map entity name -> (join descriptor -> element describing the "versioned" join)
		private readonly IDictionary<string, IDictionary<Join, XmlElement>> entitiesJoins;

		public AuditMetadataGenerator(Cfg.Configuration cfg,
										GlobalConfiguration globalCfg,
										AuditEntitiesConfiguration verEntCfg,
										IAuditStrategy auditStrategy,
										XmlElement revisionInfoRelationMapping,
										AuditEntityNameRegister auditEntityNameRegister)
		{
			Cfg = cfg;
			GlobalCfg = globalCfg;
			VerEntCfg = verEntCfg;
			AuditStrategy = auditStrategy;
			this.revisionInfoRelationMapping = revisionInfoRelationMapping;
			BasicMetadataGenerator = new BasicMetadataGenerator();
			componentMetadataGenerator = new ComponentMetadataGenerator(this);
			idMetadataGenerator = new IdMetadataGenerator(this);
			toOneRelationMetadataGenerator = new ToOneRelationMetadataGenerator(this);
			AuditEntityNameRegister = auditEntityNameRegister;
			EntitiesConfigurations = new Dictionary<string, EntityConfiguration>();
			NotAuditedEntitiesConfigurations = new Dictionary<string, EntityConfiguration>();
			entitiesJoins = new Dictionary<string, IDictionary<Join, XmlElement>>();
		}


		/// <summary>
		///  Clones the revision info relation mapping, so that it can be added to other mappings. Also, the name of
		///  the property and the column are set properly.
		/// </summary>
		/// <param name="doc">The xml document</param>
		/// <returns>A revision info mapping, which can be added to other mappings (has no parent).</returns>
		private XmlElement CloneAndSetupRevisionInfoRelationMapping(XmlDocument doc)
		{
			var revMapping = (XmlElement)doc.ImportNode(revisionInfoRelationMapping, true);
			revMapping.SetAttribute("name", VerEntCfg.RevisionFieldName);

			MetadataTools.AddOrModifyColumn(revMapping, VerEntCfg.RevisionFieldName);

			return revMapping;
		}

		public void AddRevisionInfoRelation(XmlElement anyMapping)
		{
			anyMapping.AppendChild(CloneAndSetupRevisionInfoRelationMapping(anyMapping.OwnerDocument));
		}

		public void AddRevisionType(XmlElement anyMapping)
		{
			var revTypeProperty = MetadataTools.AddProperty(anyMapping, VerEntCfg.RevisionTypePropName,
					VerEntCfg.RevisionTypePropType, true, false);
			revTypeProperty.SetAttribute("type", typeof(RevisionTypeType).AssemblyQualifiedName);
			revTypeProperty.SetAttribute("not-null", "true");
			addEndRevision(anyMapping);
		}

		private void addEndRevision(XmlElement anyMapping)
		{
			// Add the end-revision field, if the appropriate strategy is used.
			if (VerEntCfg.AuditStrategyType == typeof(ValidityAuditStrategy))
			{
				MetadataTools.AddManyToOne(anyMapping, VerEntCfg.RevisionEndFieldName, VerEntCfg.RevisionInfoEntityAssemblyQualifiedName, true, true);

				if (VerEntCfg.IsRevisionEndTimestampEnabled)
				{
					const string revisionInfoTimestampSqlType = "Timestamp";
					MetadataTools.AddProperty(anyMapping, VerEntCfg.RevisionEndTimestampFieldName, revisionInfoTimestampSqlType, true, true, false);
				}
			}
		}



		private void addValueInFirstPass(XmlElement parent, IValue value, ICompositeMapperBuilder currentMapper, string entityName,
					  EntityXmlMappingData xmlMappingData, PropertyAuditingData propertyAuditingData,
					  bool insertable, bool processModifiedFlag)
		{
			var type = value.Type;

			if (BasicMetadataGenerator.AddBasic(parent, propertyAuditingData, value, currentMapper, insertable, false))
			{
				// The property was mapped by the basic generator.
			}
			else if (type is ComponentType)
			{
				componentMetadataGenerator.AddComponent(parent, propertyAuditingData, value, currentMapper, entityName,
																	 xmlMappingData, true, insertable);
			}
			else
			{
				if (!processedInSecondPass(type))
				{
					// If we got here in the first pass, it means the basic mapper didn't map it, and none of the
					// above branches either.
					ThrowUnsupportedTypeException(type, entityName, propertyAuditingData.Name);
				}
				return;
			}
			addModifiedFlagIfNeeded(parent, propertyAuditingData, processModifiedFlag);
		}

		private static bool processedInSecondPass(IType type)
		{
			return type is ComponentType ||
					 type is ManyToOneType ||
					 type is OneToOneType ||
					 type is CollectionType;
		}

		private void addValueInSecondPass(XmlElement parent, IValue value, ICompositeMapperBuilder currentMapper, string entityName,
																									  EntityXmlMappingData xmlMappingData, PropertyAuditingData propertyAuditingData,
																									  bool insertable, bool processModifiedFlag)
		{
			var type = value.Type;

			if (type is ComponentType)
			{
				componentMetadataGenerator.AddComponent(parent, propertyAuditingData, value, currentMapper,
						entityName, xmlMappingData, false, insertable);
				return;// mod flag field has been already generated in first pass
			}
			else if (type is ManyToOneType)
			{
				toOneRelationMetadataGenerator.AddToOne(parent, propertyAuditingData, value, currentMapper, entityName, insertable);
			}
			else if (type is OneToOneType)
			{
				var oneToOne = (OneToOne)value;
				if (oneToOne.ReferencedPropertyName != null)
				{
					toOneRelationMetadataGenerator.AddOneToOneNotOwning(propertyAuditingData, value, currentMapper, entityName);	
				}
				else
				{
					// @OneToOne relation marked with @PrimaryKeyJoinColumn
					toOneRelationMetadataGenerator.AddOneToOnePrimaryKeyJoinColumn(propertyAuditingData, value,
																		currentMapper, entityName, insertable);
				}
			}
			else if (type is CollectionType)
			{
				var collectionMetadataGenerator = new CollectionMetadataGenerator(this, (Mapping.Collection) value, currentMapper, entityName,
																										xmlMappingData, propertyAuditingData);
				collectionMetadataGenerator.AddCollection();
			}
			else
			{
				return;
			}
			addModifiedFlagIfNeeded(parent, propertyAuditingData, processModifiedFlag);
		}

		private void addModifiedFlagIfNeeded(XmlElement parent, PropertyAuditingData propertyAuditingData, bool processModifiedFlag)
		{
			if (processModifiedFlag && propertyAuditingData.UsingModifiedFlag)
			{
				MetadataTools.AddModifiedFlagProperty(parent, propertyAuditingData.Name, GlobalCfg.ModifiedFlagSuffix);
			}
		}

		public void AddValue(XmlElement parent, IValue value, ICompositeMapperBuilder currentMapper, string entityName,
												 EntityXmlMappingData xmlMappingData, PropertyAuditingData propertyAuditingData,
												 bool insertable, bool firstPass, bool processModifiedFlag)
		{
			if (firstPass)
			{
				addValueInFirstPass(parent, value, currentMapper, entityName, xmlMappingData, propertyAuditingData, insertable, processModifiedFlag);
			}
			else
			{
				addValueInSecondPass(parent, value, currentMapper, entityName, xmlMappingData, propertyAuditingData, insertable, processModifiedFlag);
			}
		}

		private void AddProperties(XmlElement parent, IEnumerable<Property> properties, ICompositeMapperBuilder currentMapper,
									ClassAuditingData auditingData, string entityName, EntityXmlMappingData xmlMappingData,
									bool firstPass)
		{
			foreach (var property in properties)
			{
				var propertyName = property.Name;
				var propertyAuditingData = auditingData.GetPropertyAuditingData(propertyName);
				if (propertyAuditingData != null)
				{
					AddValue(parent, property.Value, currentMapper, entityName, xmlMappingData, propertyAuditingData,
							property.IsInsertable, firstPass, true);
				}
			}
		}

		private static bool CheckPropertiesAudited(IEnumerable<Property> properties, ClassAuditingData auditingData)
		{
			foreach (var property in properties)
			{
				var propertyName = property.Name;
				var propertyAuditingData = auditingData.GetPropertyAuditingData(propertyName);
				if (propertyAuditingData == null)
				{
					return false;
				}
			}

			return true;
		}

		public string GetSchema(string schemaFromAnnotation, Table table)
		{
			// Get the schema from the annotation ...
			var schema = schemaFromAnnotation;
			// ... if empty, try using the default ...
			if (string.IsNullOrEmpty(schema))
			{
				schema = GlobalCfg.DefaultSchemaName;

				// ... if still empty, use the same as the normal table.
				if (string.IsNullOrEmpty(schema))
				{
					schema = table.Schema;
				}
			}
			return schema;
		}

		public string GetCatalog(string catalogFromAnnotation, Table table)
		{
			// Get the catalog from the annotation ...
			var catalog = catalogFromAnnotation;
			// ... if empty, try using the default ...
			if (string.IsNullOrEmpty(catalog))
			{
				catalog = GlobalCfg.DefaultCatalogName;

				// ... if still empty, use the same as the normal table.
				if (string.IsNullOrEmpty(catalog))
				{
					catalog = table.Catalog;
				}
			}
			return catalog;
		}

		private void CreateJoins(PersistentClass pc, XmlElement parent, ClassAuditingData auditingData)
		{
			var JoinElements = new Dictionary<Join, XmlElement>();
			entitiesJoins.Add(pc.EntityName, JoinElements);

			foreach (var join in pc.JoinIterator)
			{
				// Checking if all of the join properties are audited
				if (!CheckPropertiesAudited(join.PropertyIterator, auditingData))
				{
					continue;
				}

				// Determining the table name. If there is no entry in the dictionary, just constructing the table name
				// as if it was an entity (by appending/prepending configured strings).
				var originalTableName = join.Table.Name;
				string auditTableName;
				if (!auditingData.JoinTableDictionary.TryGetValue(originalTableName, out auditTableName))
				{
					auditTableName = VerEntCfg.GetAuditEntityName(originalTableName);
				}

				var schema = GetSchema(auditingData.AuditTable.Schema, join.Table);
				var catalog = GetCatalog(auditingData.AuditTable.Catalog, join.Table);

				var joinElement = MetadataTools.CreateJoin(parent, auditTableName, schema, catalog);
				JoinElements.Add(join, joinElement);

				var joinKey = joinElement.OwnerDocument.CreateElement("key");
				joinElement.AppendChild(joinKey);
				MetadataTools.AddColumns(joinKey, join.Key.ColumnIterator.OfType<Column>());
				MetadataTools.AddColumn(joinKey, VerEntCfg.RevisionFieldName, -1, -1, -1, null, false);
			}
		}

		private void AddJoins(PersistentClass pc, ICompositeMapperBuilder currentMapper, ClassAuditingData auditingData,
							  string entityName, EntityXmlMappingData xmlMappingData, bool firstPass)
		{
			foreach (var join in pc.JoinIterator)
			{
				var joinElement = entitiesJoins[entityName][join];

				if (joinElement != null)
				{
					AddProperties(joinElement, join.PropertyIterator, currentMapper, auditingData, entityName,
							xmlMappingData, firstPass);
				}
			}
		}

		private Triple<XmlElement, IExtendedPropertyMapper, string> GenerateMappingData(
				PersistentClass pc, EntityXmlMappingData xmlMappingData, AuditTableData auditTableData,
				IdMappingData idMapper)
		{
			var hasDiscriminator = pc.Discriminator != null;

			var classMapping = MetadataTools.CreateEntity(xmlMappingData.MainXmlMapping, auditTableData,
					hasDiscriminator ? pc.DiscriminatorValue : null);
			var propertyMapper = new MultiPropertyMapper();

			// Adding the id mapping
			var xmlMp = classMapping.OwnerDocument.ImportNode(idMapper.XmlMapping, true);
			classMapping.AppendChild(xmlMp);

			// Checking if there is a discriminator column
			if (hasDiscriminator)
			{
				var discriminatorElement = classMapping.OwnerDocument.CreateElement("discriminator");
				classMapping.AppendChild(discriminatorElement);
				// Database column or SQL formula allowed to distinguish entity types
				MetadataTools.AddColumnsOrFormulas(discriminatorElement, pc.Discriminator.ColumnIterator);
				discriminatorElement.SetAttribute("type", pc.Discriminator.Type.Name);
			}

			// Adding the "revision type" property
			AddRevisionType(classMapping);

			return new Triple<XmlElement, IExtendedPropertyMapper, string>(classMapping, propertyMapper, null);
		}

		private Triple<XmlElement, IExtendedPropertyMapper, string> GenerateInheritanceMappingData(
				PersistentClass pc, EntityXmlMappingData xmlMappingData, AuditTableData auditTableData,
				string inheritanceMappingType)
		{
			var extendsEntityName = VerEntCfg.GetAuditEntityName(pc.Superclass.EntityName);
			var hasDiscriminator = pc.Discriminator != null;
			var classMapping = MetadataTools.CreateSubclassEntity(xmlMappingData.MainXmlMapping,
					inheritanceMappingType, auditTableData, extendsEntityName, hasDiscriminator ? pc.DiscriminatorValue : null);

			// The id and revision type is already mapped in the parent

			// Getting the property mapper of the parent - when mapping properties, they need to be included
			var parentEntityName = pc.Superclass.EntityName;

			EntityConfiguration parentConfiguration;
			if (!EntitiesConfigurations.TryGetValue(parentEntityName, out parentConfiguration))
			{
				throw new MappingException("Entity '" + pc.EntityName + "' is audited, but its superclass: '" + parentEntityName + "' is not.");
			}

			var parentPropertyMapper = parentConfiguration.PropertyMapper;
			var propertyMapper = new SubclassPropertyMapper(new MultiPropertyMapper(), parentPropertyMapper);

			return new Triple<XmlElement, IExtendedPropertyMapper, string>(classMapping, propertyMapper, parentEntityName);
		}

		public void GenerateFirstPass(PersistentClass pc, ClassAuditingData auditingData,
									  EntityXmlMappingData xmlMappingData, bool isAudited)
		{
			var schema = GetSchema(auditingData.AuditTable.Schema, pc.Table);
			var catalog = GetCatalog(auditingData.AuditTable.Catalog, pc.Table);

			var entityName = pc.EntityName;
			if (!isAudited)
			{
				var _idMapper = idMetadataGenerator.AddId(pc);

				if (_idMapper == null)
				{
					// Unsupported id mapping, e.g. key-many-to-one. If the entity is used in auditing, an exception
					// will be thrown later on.
					if (log.IsDebugEnabled)
					{
						log.DebugFormat("Unable to create auditing id mapping for entity {0}" +
							", because of an unsupported Hibernate id mapping (e.g. key-many-to-one).", entityName);
					}
					return;
				}

				//ORIG:
				//IExtendedPropertyMapper propertyMapper = null;
				//String parentEntityName = null;
				var _entityCfg = new EntityConfiguration(entityName, pc.ClassName, _idMapper, null, null);
				NotAuditedEntitiesConfigurations.Add(entityName, _entityCfg);
				return;
			}

			if (log.IsDebugEnabled)
			{
				log.DebugFormat("Generating first-pass auditing mapping for entity {0}.", entityName);
			}

			var auditEntityName = VerEntCfg.GetAuditEntityName(entityName);
			var auditTableName = VerEntCfg.GetAuditTableName(entityName, pc.Table.Name);

			// Registering the audit entity name, now that it is known
			AuditEntityNameRegister.Register(auditEntityName);

			var auditTableData = new AuditTableData(auditEntityName, auditTableName, schema, catalog);

			// Generating a mapping for the id
			var idMapper = idMetadataGenerator.AddId(pc);

			var inheritanceType = pc.GetInheritanceType();

			// These properties will be read from the mapping data

			Triple<XmlElement, IExtendedPropertyMapper, string> mappingData;

			// Reading the mapping data depending on inheritance type (if any)
			switch (inheritanceType)
			{
				case InheritanceType.None:
					mappingData = GenerateMappingData(pc, xmlMappingData, auditTableData, idMapper);
					break;

				case InheritanceType.Single:
					auditTableData = new AuditTableData(auditEntityName, null, schema, catalog);
					mappingData = GenerateInheritanceMappingData(pc, xmlMappingData, auditTableData, "subclass");
					break;

				case InheritanceType.Joined:
					mappingData = GenerateInheritanceMappingData(pc, xmlMappingData, auditTableData, "joined-subclass");

					// Adding the "key" element with all id columns...
					var keyMapping = mappingData.First.OwnerDocument.CreateElement("key");
					mappingData.First.AppendChild(keyMapping);
					MetadataTools.AddColumns(keyMapping, pc.Table.PrimaryKey.ColumnIterator);

					// ... and the revision number column, read from the revision info relation mapping.
					keyMapping.AppendChild(CloneAndSetupRevisionInfoRelationMapping(keyMapping.OwnerDocument).GetElementsByTagName("column")[0].Clone());
					break;

				case InheritanceType.TablePerClass:
					mappingData = GenerateInheritanceMappingData(pc, xmlMappingData, auditTableData, "union-subclass");

					break;

				default:
					throw new AssertionFailure("AuditMetadataGenerator.GenerateFirstPass: Impossible enum value.");
			}

			var class_mapping = mappingData.First;
			var propertyMapper = mappingData.Second;
			var parentEntityName = mappingData.Third;

			xmlMappingData.ClassMapping = class_mapping;

			// Mapping unjoined properties
			AddProperties(class_mapping, pc.UnjoinedPropertyIterator, propertyMapper,
					auditingData, pc.EntityName, xmlMappingData,
					true);

			// Creating and mapping joins (first pass)
			CreateJoins(pc, class_mapping, auditingData);
			AddJoins(pc, propertyMapper, auditingData, pc.EntityName, xmlMappingData, true);

			// Storing the generated configuration
			var entityCfg = new EntityConfiguration(auditEntityName, pc.ClassName, idMapper,
					propertyMapper, parentEntityName);
			EntitiesConfigurations.Add(pc.EntityName, entityCfg);
		}

		public void GenerateSecondPass(PersistentClass pc, ClassAuditingData auditingData,
										EntityXmlMappingData xmlMappingData)
		{
			var entityName = pc.EntityName;
			if (log.IsDebugEnabled)
			{
				log.DebugFormat("Generating second-pass auditing mapping for entity {0}.", entityName);
			}

			var propertyMapper = EntitiesConfigurations[entityName].PropertyMapper;

			// Mapping unjoined properties
			var parent = xmlMappingData.ClassMapping;

			AddProperties(parent, pc.UnjoinedPropertyIterator,
					propertyMapper, auditingData, entityName, xmlMappingData, false);

			// Mapping joins (second pass)
			AddJoins(pc, propertyMapper, auditingData, entityName, xmlMappingData, false);
		}

		// Getters for generators and configuration

		public void ThrowUnsupportedTypeException(IType type, string entityName, string propertyName)
		{
			var message = "Type not supported for auditing: " + type.Name +
					", on entity " + entityName + ", property '" + propertyName + "'.";

			throw new MappingException(message);
		}

		/**
		 * Reads the id mapping data of a referenced entity.
		 * @param entityName Name of the entity which is the source of the relation.
		 * @param referencedEntityName Name of the entity which is the target of the relation.
		 * @param propertyAuditingData Auditing data of the property that is the source of the relation.
		 * @param allowNotAuditedTarget Are not-audited target entities allowed.
		 * @throws MappingException If a relation from an audited to a non-audited entity is detected, which is not
		 * mapped using {@link RelationTargetAuditMode#NotAudited}.
		 * @return The id mapping data of the related entity. 
		 */
		public IdMappingData GetReferencedIdMappingData(string entityName, string referencedEntityName,
												PropertyAuditingData propertyAuditingData,
												bool allowNotAuditedTarget)
		{
			EntityConfiguration configuration;
			if (EntitiesConfigurations.Keys.Contains(referencedEntityName))
				configuration = EntitiesConfigurations[referencedEntityName];
			else
			{
				var relationTargetAuditMode = propertyAuditingData.RelationTargetAuditMode;

				if (!NotAuditedEntitiesConfigurations.Keys.Contains(referencedEntityName) ||
					!allowNotAuditedTarget || !RelationTargetAuditMode.NotAudited.Equals(relationTargetAuditMode))
				{
					throw new MappingException("An audited relation from " + entityName + "."
							+ propertyAuditingData.Name + " to a not audited entity " + referencedEntityName + "!"
							+ (allowNotAuditedTarget ?
								" Such mapping is possible, but has to be explicitly defined using [Audited(TargetAuditMode = RelationTargetAuditMode.NotAudited)]." :
								string.Empty));
				}
				configuration = NotAuditedEntitiesConfigurations[referencedEntityName];
			}
			return configuration.IdMappingData;
		}
	}
}

