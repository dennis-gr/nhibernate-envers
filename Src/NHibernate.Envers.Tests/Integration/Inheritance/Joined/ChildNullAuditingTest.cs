using NHibernate.Envers.Tests.Integration.Inheritance.Entities;
using NUnit.Framework;

namespace NHibernate.Envers.Tests.Integration.Inheritance.Joined
{
	public class ChildNullAuditingTest : TestBase
	{
		private int id1;

		protected override void Initialize()
		{
			id1 = 1;
			var ce = new ChildEntity {Id = id1, Data = "x"};
			using (var tx = Session.BeginTransaction())
			{
				Session.Save(ce);
				tx.Commit();
			}
			using (var tx =Session.BeginTransaction())
			{
				ce.Data = null;
				ce.Number = 2;
				tx.Commit();
			}
		}

		[Test]
		public void VerifyRevisionCount()
		{
			CollectionAssert.AreEquivalent(new[] { 1, 2 }, AuditReader().GetRevisions(typeof(ChildEntity), id1));
		}

		[Test]
		public void VerifyHistoryOfChild()
		{
			var ver1 = new ChildEntity { Id = id1, Data = "x", Number = null };
			var ver2 = new ChildEntity { Id = id1, Data = null, Number = 2 };

			Assert.AreEqual(ver1, AuditReader().Find<ChildEntity>(id1, 1));
			Assert.AreEqual(ver2, AuditReader().Find<ChildEntity>(id1, 2));


			Assert.AreEqual(ver1, AuditReader().Find<ParentEntity>(id1, 1));
			Assert.AreEqual(ver2, AuditReader().Find<ParentEntity>(id1, 2));
		}

		[Test]
		public void VerifyPolymorphicQuery()
		{
			var childVersion1 = new ChildEntity { Id = id1, Data = "x", Number = null };
			Assert.AreEqual(childVersion1, AuditReader().CreateQuery().ForEntitiesAtRevision(typeof(ChildEntity), 1).GetSingleResult());
			Assert.AreEqual(childVersion1, AuditReader().CreateQuery().ForEntitiesAtRevision(typeof(ParentEntity), 1).GetSingleResult());
		}
	}
}