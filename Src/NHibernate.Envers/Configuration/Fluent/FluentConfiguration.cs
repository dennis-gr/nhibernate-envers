using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using NHibernate.Envers.Configuration.Attributes;
using NHibernate.Envers.Configuration.Store;

namespace NHibernate.Envers.Configuration.Fluent
{
	/// <summary>
	/// This implementation of <see cref="IMetaDataProvider"/>
	/// is used to programmaticly configure envers.
	/// </summary>
	public class FluentConfiguration : IMetaDataProvider
	{
		private static readonly IInternalLogger log = LoggerProvider.LoggerFor(typeof (FluentConfiguration));
		private readonly IList<IAttributeProvider> attributeFactories;
		private readonly ICollection<System.Type> auditedTypes;

		public FluentConfiguration()
		{
			attributeFactories = new List<IAttributeProvider>();
			auditedTypes = new List<System.Type>();
		}

		/// <summary>
		/// Register entity type T to be audited.
		/// </summary>
		/// <typeparam name="T">The type to be audited.</typeparam>
		/// <returns>A fluent audit object where you can fine grain the auditing.</returns>
		public IFluentAudit<T> Audit<T>()
		{
			var ret = new FluentAudit<T>();
			attributeFactories.Add(ret);
			auditedTypes.Add(typeof(T));
			return ret;
		}

		/// <summary>
		/// Register multiple audited entities.
		/// </summary>
		/// <param name="types">All types to be audited.</param>
		/// <remarks>
		/// Each class will be audited using default values of <see cref="AuditedAttribute"/> without exclusions of properties.
		/// </remarks>
		public void Audit(IEnumerable<System.Type> types)
		{
			foreach (var type in types)
			{
				attributeFactories.Add(new LooselyTypedFluentAudit(type));
				auditedTypes.Add(type);
			}
		}

		/// <summary>
		/// Defines a custom revision entity.
		/// </summary>
		/// <typeparam name="T">The custom revision entity type</typeparam>
		/// <param name="revisionNumber">Revision number property on custom revision entity</param>
		/// <param name="revisionTimestamp">Revision timestamp property on custom revision entity</param>
		public void SetRevisionEntity<T>(Expression<Func<T, object>> revisionNumber, Expression<Func<T, object>> revisionTimestamp)
		{
			attributeFactories.Add(new FluentRevision(typeof (T), 
								revisionNumber.Body.MethodInfo("revisionNumber"),
								revisionTimestamp.Body.MethodInfo("revisionTimestamp")));
		}

		IDictionary<System.Type, IEntityMeta> IMetaDataProvider.CreateMetaData(Cfg.Configuration nhConfiguration)
		{
			var ret = new Dictionary<System.Type, IEntityMeta>();
			foreach (var attributeFactory in attributeFactories)
			{
				var type = attributeFactory.Type;
				foreach (var classAttribute in attributeFactory.CreateClassAttributes())
				{
					var entMeta = createOrGetEntityMeta(ret, type);
					log.Debug("Adding " + classAttribute.GetType().Name + " to type " + type.FullName);
					entMeta.AddClassMeta(classAttribute);
				}
				foreach (var memberInfoAndAttribute in attributeFactory.CreateMemberAttributes())
				{
					var entMeta = createOrGetEntityMeta(ret, type);
					log.Debug("Adding " + memberInfoAndAttribute.Attribute.GetType().Name + " to member " + memberInfoAndAttribute.MemberInfo.Name + " on type " + type.FullName);
					entMeta.AddMemberMeta(memberInfoAndAttribute.MemberInfo, memberInfoAndAttribute.Attribute);	
				}
			}
			addBaseTypesForAuditAttribute(ret, auditedTypes);
			return ret;
		}

		private static void addBaseTypesForAuditAttribute(IDictionary<System.Type, IEntityMeta> ret, IEnumerable<System.Type> auditedTypes)
		{
			foreach (var auditedType in auditedTypes)
			{
				setBaseTypeAsAudited(auditedType.BaseType, ret);
			}
		}

		private static void setBaseTypeAsAudited(System.Type baseType, IDictionary<System.Type, IEntityMeta> ret)
		{
			if (baseType.Equals(typeof(object)))
				return;

			IEntityMeta entMetaForBaseTypeTemp;
			if(!ret.TryGetValue(baseType, out entMetaForBaseTypeTemp))
			{
				entMetaForBaseTypeTemp = new EntityMeta();
			}
			var entMetaForBaseType = (EntityMeta) entMetaForBaseTypeTemp;
			if(!entityMetaIsAuditedClass(entMetaForBaseType))
			{
				entMetaForBaseType.AddClassMeta(new AuditedAttribute());
				ret[baseType] = entMetaForBaseType;
			}
			setBaseTypeAsAudited(baseType.BaseType, ret);
		}

		private static bool entityMetaIsAuditedClass(EntityMeta entMetaForBaseType)
		{
			foreach (var classMeta in entMetaForBaseType.ClassMetas)
			{
				if (classMeta.GetType().Equals(typeof(AuditedAttribute)))
					return true;
			}
			return false;
		}

		private static EntityMeta createOrGetEntityMeta(IDictionary<System.Type, IEntityMeta> metas, System.Type type)
		{
			IEntityMeta ret;
			if (!metas.TryGetValue(type, out ret))
			{
				ret = new EntityMeta();
				metas[type] = ret;
			}
			return (EntityMeta)ret;
		}
	}
}