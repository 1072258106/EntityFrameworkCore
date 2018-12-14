﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal
{
    /// <summary>
    ///     Rewrites collection navigations into subqueries, e.g.:
    ///     customers.Select(c => c.Order.OrderDetails.Where(...)) => customers.Select(c => orderDetails.Where(od => od.OrderId == c.Order.Id).Where(...))
    /// </summary>
    public class CollectionNavigationRewritingExpressionVisitor : LinqQueryExpressionVisitorBase
    {
        private readonly IModel _model;

        public CollectionNavigationRewritingExpressionVisitor(IModel model)
        {
            _model = model;
        }

        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            // don't touch Include
            // this is temporary, new nav expansion happens to early at the moment
            if (methodCallExpression.IsIncludeMethod())
            {
                return methodCallExpression;
            }

            if (methodCallExpression.Method.MethodIsClosedFormOf(QueryableSelectManyMethodInfo))
            {
                return methodCallExpression;
            }

            if (methodCallExpression.Method.MethodIsClosedFormOf(QueryableSelectManyWithResultOperatorMethodInfo))
            {
                var newResultSelector = Visit(methodCallExpression.Arguments[2]);

                return newResultSelector != methodCallExpression.Arguments[2]
                    ? methodCallExpression.Update(methodCallExpression.Object, new[] { methodCallExpression.Arguments[0], methodCallExpression.Arguments[1], newResultSelector })
                    : methodCallExpression;
            }

            return base.VisitMethodCall(methodCallExpression);
        }

        protected override Expression VisitMember(MemberExpression memberExpression)
        {
            var binding = NavigationPropertyBinder.BindNavigationProperties(memberExpression, _model);
            if (binding.navigations.Any()
                && binding.navigations.Last() is INavigation navigation
                && navigation.IsCollection())
            {
                var collectionNavigationElementType = navigation.ForeignKey.DeclaringEntityType.ClrType;
                var entityQueryable = NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(collectionNavigationElementType);

                var outerExpression = memberExpression.Expression;

                var outerKeyAccess = CreateKeyAccessExpression(
                    outerExpression,
                    navigation.ForeignKey.PrincipalKey.Properties);

                var innerParameter = Expression.Parameter(collectionNavigationElementType, collectionNavigationElementType.Name.ToLower().Substring(0, 1));
                var innerKeyAccess = CreateKeyAccessExpression(
                    innerParameter,
                    navigation.ForeignKey.Properties);

                var predicate = Expression.Lambda(
                    CreateKeyComparisonExpressionForCollectionNavigationSubquery(
                        outerKeyAccess,
                        innerKeyAccess,
                        outerExpression,
                        binding.root,
                        binding.navigations),
                    innerParameter);

                return Expression.Call(
                    QueryableWhereMethodInfo.MakeGenericMethod(collectionNavigationElementType),
                    entityQueryable,
                    predicate);
            }

            return base.VisitMember(memberExpression);
        }

        //protected override Expression VisitExtension(Expression extensionExpression)
        //{
        //    if (extensionExpression is NavigationBindingExpression navigationBindingExpression
        //        && navigationBindingExpression.Navigations.Last() is INavigation navigation
        //        && navigation.IsCollection())
        //    {
        //        var collectionNavigationElementType = navigation.ForeignKey.DeclaringEntityType.ClrType;
        //        var entityQueryable = NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(collectionNavigationElementType);

        //        // unwrap top level expression - since we are inside nav NavigationPropertyBindingExpression the top level will either be member expression or EF.Property
        //        var outerExpression = (navigationBindingExpression.Operand as MemberExpression)?.Expression
        //            ?? (navigationBindingExpression.Operand as MethodCallExpression).Arguments[0];

        //        var outerNavigationBinding = new NavigationBindingExpression(
        //            outerExpression,
        //            navigationBindingExpression.Root,
        //            navigationBindingExpression.Navigations.Take(navigationBindingExpression.Navigations.Count - 1));

        //        var outerKeyAccess = CreateKeyAccessExpression(
        //            outerNavigationBinding,
        //            navigation.ForeignKey.PrincipalKey.Properties);

        //        var innerParameter = Expression.Parameter(collectionNavigationElementType, collectionNavigationElementType.Name.ToLower().Substring(0, 1));
        //        var innerKeyAccess = CreateKeyAccessExpression(
        //            innerParameter,
        //            navigation.ForeignKey.Properties);

        //        var predicate = Expression.Lambda(
        //            CreateKeyComparisonExpressionForCollectionNavigationSubquery(
        //                outerKeyAccess,
        //                innerKeyAccess,
        //                outerNavigationBinding,
        //                navigationBindingExpression.Root,
        //                navigationBindingExpression.Navigations),
        //            innerParameter);

        //        return Expression.Call(
        //            QueryableWhereMethodInfo.MakeGenericMethod(collectionNavigationElementType),
        //            entityQueryable,
        //            predicate);
        //    }

        //    return extensionExpression;
        //}

        private static Expression CreateKeyAccessExpression(
            Expression target, IReadOnlyList<IProperty> properties, bool addNullCheck = false)
            => properties.Count == 1
                ? CreatePropertyExpression(target, properties[0], addNullCheck)
                : Expression.New(
                    AnonymousObject.AnonymousObjectCtor,
                    Expression.NewArrayInit(
                        typeof(object),
                        properties
                            .Select(p => Expression.Convert(CreatePropertyExpression(target, p, addNullCheck), typeof(object)))
                            .Cast<Expression>()
                            .ToArray()));

        private static Expression CreatePropertyExpression(Expression target, IProperty property, bool addNullCheck)
        {
            var propertyExpression = target.CreateEFPropertyExpression(property, makeNullable: false);

            var propertyDeclaringType = property.DeclaringType.ClrType;
            if (propertyDeclaringType != target.Type
                && target.Type.GetTypeInfo().IsAssignableFrom(propertyDeclaringType.GetTypeInfo()))
            {
                if (!propertyExpression.Type.IsNullableType())
                {
                    propertyExpression = Expression.Convert(propertyExpression, propertyExpression.Type.MakeNullable());
                }

                return Expression.Condition(
                    Expression.TypeIs(target, propertyDeclaringType),
                    propertyExpression,
                    Expression.Constant(null, propertyExpression.Type));
            }

            return addNullCheck
                ? new NullConditionalExpression(target, propertyExpression)
                : propertyExpression;
        }

        private static Expression CreateKeyComparisonExpressionForCollectionNavigationSubquery(
            Expression outerKeyExpression,
            Expression innerKeyExpression,
            Expression colectionRootExpression,
            Expression navigationRootExpression,
            IEnumerable<INavigation> navigations)
        {
            if (outerKeyExpression.Type != innerKeyExpression.Type)
            {
                if (outerKeyExpression.Type.IsNullableType())
                {
                    Debug.Assert(outerKeyExpression.Type.UnwrapNullableType() == innerKeyExpression.Type);

                    innerKeyExpression = Expression.Convert(innerKeyExpression, outerKeyExpression.Type);
                }
                else
                {
                    Debug.Assert(innerKeyExpression.Type.IsNullableType());
                    Debug.Assert(innerKeyExpression.Type.UnwrapNullableType() == outerKeyExpression.Type);

                    outerKeyExpression = Expression.Convert(outerKeyExpression, innerKeyExpression.Type);
                }
            }

            var outerNullProtection
                = Expression.NotEqual(
                    colectionRootExpression,
                    Expression.Constant(null, colectionRootExpression.Type));

            return new NullSafeEqualExpression(
                outerNullProtection,
                Expression.Equal(outerKeyExpression, innerKeyExpression));
        }
    }
}
