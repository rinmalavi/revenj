using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.Contracts;
using System.Linq;
using Remotion.Linq;
using Remotion.Linq.Clauses.ResultOperators;
using Revenj.DatabasePersistence.Postgres.QueryGeneration.Visitors;
using Revenj.DomainPatterns;
using Revenj.Extensibility;

namespace Revenj.DatabasePersistence.Postgres.QueryGeneration
{
	public class QueryExecutor : IQueryExecutor
	{
		private readonly IDatabaseQuery DatabaseQuery;
		private readonly IServiceLocator Locator;
		private readonly IPostgresConverterFactory ConverterFactory;
		private readonly IExtensibilityProvider ExtensibilityProvider;

		public QueryExecutor(IDatabaseQuery databaseQuery, IServiceLocator locator)
			: this(databaseQuery, locator, locator.Resolve<IPostgresConverterFactory>(), locator.Resolve<IExtensibilityProvider>())
		{
		}

		public QueryExecutor(
			IDatabaseQuery databaseQuery,
			IServiceLocator locator,
			IPostgresConverterFactory factory,
			IExtensibilityProvider extensibilityProvider)
		{
			Contract.Requires(databaseQuery != null);
			Contract.Requires(locator != null);
			Contract.Requires(factory != null);
			Contract.Requires(extensibilityProvider != null);

			this.DatabaseQuery = databaseQuery;
			this.Locator = locator;
			this.ConverterFactory = factory;
			this.ExtensibilityProvider = extensibilityProvider;
		}

		// Executes a query with a scalar result, i.e. a query that ends with a result operator such as Count, Sum, or Average.
		public T ExecuteScalar<T>(QueryModel queryModel)
		{
			var seedAggregate = queryModel.ResultOperators.LastOrDefault(it => it is AggregateFromSeedResultOperator) as AggregateFromSeedResultOperator;
			if (seedAggregate != null)
				return ExecuteSeedAggregate<T>(queryModel, seedAggregate);
			var aggregate = queryModel.ResultOperators.LastOrDefault(it => it is AggregateResultOperator) as AggregateResultOperator;
			if (aggregate != null)
				return ExecuteAggregate<T>(queryModel, aggregate);
			var sum = queryModel.ResultOperators.LastOrDefault(it => it is SumResultOperator) as SumResultOperator;
			if (sum != null)
				return ExecuteSum<T>(queryModel);
			var average = queryModel.ResultOperators.LastOrDefault(it => it is AverageResultOperator) as AverageResultOperator;
			if (average != null)
			{
				var calcType = typeof(CalculateAverage<,>).MakeGenericType(typeof(T), queryModel.SelectClause.Selector.Type);
				Func<List<ResultObjectMapping>> getData = () => LoadData(queryModel);
				var calculate = (ICalculateAvergae<T>)Activator.CreateInstance(calcType, new object[] { getData });
				return calculate.Calculate(queryModel);
			}
			return ExecuteCollection<T>(queryModel).Single();
		}

		interface ICalculateAvergae<T>
		{
			T Calculate(QueryModel queryModel);
		}

		class CalculateAverage<TResult, TInput> : ICalculateAvergae<TResult>
		{
			private readonly Func<List<ResultObjectMapping>> GetData;

			public CalculateAverage(Func<List<ResultObjectMapping>> getData)
			{
				this.GetData = getData;
			}

			public TResult Calculate(QueryModel queryModel)
			{
				var projector = ProjectorBuildingExpressionTreeVisitor<TInput>.BuildProjector(queryModel);
				var resultItems = GetData().Select(it => projector(it));
				var type = typeof(TInput);
				if (typeof(TInput).IsGenericType)
					type = typeof(TInput).GetGenericArguments()[0];
				if (type == typeof(decimal))
					return (TResult)((object)resultItems.Cast<decimal?>().Average());
				else if (type == typeof(long))
					return (TResult)((object)resultItems.Cast<long?>().Average());
				else if (type == typeof(int))
					return (TResult)((object)resultItems.Cast<int?>().Average());
				else if (type == typeof(double))
					return (TResult)((object)resultItems.Cast<double?>().Average());
				else if (type == typeof(float))
					return (TResult)((object)resultItems.Cast<float?>().Average());
				throw new NotSupportedException("Unknown type for sum. Supported types: decimal, long, int, double, float");
			}
		}

		private T ExecuteSum<T>(QueryModel queryModel)
		{
			var projector = ProjectorBuildingExpressionTreeVisitor<T>.BuildProjector(queryModel);
			var resultItems = LoadData(queryModel).Select(it => projector(it));
			var type = typeof(T);
			if (typeof(T).IsGenericType)
				type = typeof(T).GetGenericArguments()[0];
			if (type == typeof(decimal))
				return (T)((object)resultItems.Cast<decimal?>().Sum());
			else if (type == typeof(long))
				return (T)((object)resultItems.Cast<long?>().Sum());
			else if (type == typeof(int))
				return (T)((object)resultItems.Cast<int?>().Sum());
			else if (type == typeof(double))
				return (T)((object)resultItems.Cast<double?>().Sum());
			else if (type == typeof(float))
				return (T)((object)resultItems.Cast<float?>().Sum());
			throw new NotSupportedException("Unknown type for sum. Supported types: decimal, long, int, double, float");
		}

		private T ExecuteAggregate<T>(QueryModel queryModel, AggregateResultOperator aggregate)
		{
			var valueProjector = ProjectorBuildingExpressionTreeVisitor<T>.BuildProjector(queryModel);
			var funcProjector = ProjectorBuildingExpressionTreeVisitor<T>.BuildAggregateProjector(queryModel, aggregate.Func);
			var resultItems = LoadData(queryModel);
			if (resultItems.Count == 0)
				throw new DataException("For aggregate operation at least one result must be used");
			var result = valueProjector(resultItems[0]);
			foreach (var item in resultItems.Skip(1))
				result = funcProjector(item)(result);
			return result;
		}

		private T ExecuteSeedAggregate<T>(QueryModel queryModel, AggregateFromSeedResultOperator seedAggregate)
		{
			var projector = ProjectorBuildingExpressionTreeVisitor<T>.BuildAggregateProjector(queryModel, seedAggregate.Func);
			var resultItems = LoadData(queryModel);
			var result = seedAggregate.GetConstantSeed<T>();
			foreach (var item in resultItems)
				result = projector(item)(result);
			return result;
		}

		// Executes a query with a single result object, i.e. a query that ends with a result operator such as First, Last, Single, Min, or Max.
		public T ExecuteSingle<T>(QueryModel queryModel, bool returnDefaultWhenEmpty)
		{
			return returnDefaultWhenEmpty ? ExecuteCollection<T>(queryModel).SingleOrDefault() : ExecuteCollection<T>(queryModel).Single();
		}

		// Executes a query with a collection result.
		public IEnumerable<T> ExecuteCollection<T>(QueryModel queryModel)
		{
			var hasUnion = queryModel.ResultOperators.Any(it => it is UnionResultOperator);
			if (hasUnion)
				queryModel = queryModel.ConvertToSubQuery("sq");

			var projector = ProjectorBuildingExpressionTreeVisitor<T>.BuildProjector(queryModel);

			var resultItems = LoadData(queryModel);

			foreach (var item in resultItems)
				yield return projector(item);
		}

		private List<ResultObjectMapping> LoadData(QueryModel queryModel)
		{
			var sqlCommand =
				SqlGeneratorQueryModelVisitor.GenerateSqlQuery(
					queryModel,
					Locator,
					ConverterFactory,
					ExtensibilityProvider);

			var resultItems = new List<ResultObjectMapping>();
			DatabaseQuery.Execute(sqlCommand.CreateQuery(), dr => resultItems.Add(sqlCommand.ProcessRow(dr)));

			if (queryModel.ResultOperators.Any(it => it is LastResultOperator) && resultItems.Count > 1)
				resultItems.RemoveRange(0, resultItems.Count - 1);
			return resultItems;
		}
	}
}