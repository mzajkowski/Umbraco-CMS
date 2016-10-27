﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Umbraco.Core.Persistence.SqlSyntax;

namespace Umbraco.Core.Persistence.Querying
{

    /// <summary>
    /// This is used to determine if the expression result is cached and therefore only the SQL parameters will be extracted
    /// </summary>
    /// <remarks>
    /// This saves some performance overhead since the SQL string itself does not get generated because it already exists.
    /// </remarks>
    internal class CachedExpression : Expression
    {
        public CachedExpression()
        {
            CompiledOutput = null;
        }

        public Expression InnerExpression { get; private set; }

        /// <summary>
        /// The compiled SQL statement output
        /// </summary>
        public string CompiledOutput { get; private set; }

        public bool IsCompiled
        {
            get { return CompiledOutput.IsNullOrWhiteSpace() == false; }
        }

        public void Compile(string output)
        {
            if (IsCompiled)
                throw new InvalidOperationException("Cached expression is already compiled");

            CompiledOutput = output;
        }

        public void Wrap(Expression exp)
        {
            InnerExpression = exp;
        }
    }

    /// <summary>
    /// An expression tree parser to create SQL statements and SQL parameters based on a given strongly typed expression
    /// </summary>
    /// <remarks>
    /// Logic that is shared with the expression helpers. This object stores state, it cannot be re-used to parse an expression.
    /// </remarks>
    internal abstract class BaseExpressionHelper 
    {
        protected BaseExpressionHelper(ISqlSyntaxProvider sqlSyntax)
        {
            SqlSyntax = sqlSyntax;
        }

        /// <summary>
        /// Indicates that the SQL statement has already been compiled, so Visiting will just generate the Sql Parameters
        /// </summary>
        protected bool IsCompiled { get; set; }

        protected ISqlSyntaxProvider SqlSyntax { get; private set; }

        protected List<object> SqlParameters = new List<object>();

        protected abstract string VisitMemberAccess(MemberExpression m);

        protected internal virtual string Visit(Expression exp)
        {
            //set the flag if it is already compiled
            var compiledExp = exp as CachedExpression;
            if (compiledExp != null)
            {
                if (compiledExp.IsCompiled)
                {
                    IsCompiled = true;
                }
                exp = compiledExp.InnerExpression;
            }

            if (exp == null) return string.Empty;

            string result;

            switch (exp.NodeType)
            {
                case ExpressionType.Lambda:
                    result = VisitLambda(exp as LambdaExpression);
                    break;
                case ExpressionType.MemberAccess:
                    result = VisitMemberAccess(exp as MemberExpression);
                    break;
                case ExpressionType.Constant:
                    result = VisitConstant(exp as ConstantExpression);
                    break;
                case ExpressionType.Add:
                case ExpressionType.AddChecked:
                case ExpressionType.Subtract:
                case ExpressionType.SubtractChecked:
                case ExpressionType.Multiply:
                case ExpressionType.MultiplyChecked:
                case ExpressionType.Divide:
                case ExpressionType.Modulo:
                case ExpressionType.And:
                case ExpressionType.AndAlso:
                case ExpressionType.Or:
                case ExpressionType.OrElse:
                case ExpressionType.LessThan:
                case ExpressionType.LessThanOrEqual:
                case ExpressionType.GreaterThan:
                case ExpressionType.GreaterThanOrEqual:
                case ExpressionType.Equal:
                case ExpressionType.NotEqual:
                case ExpressionType.Coalesce:
                case ExpressionType.ArrayIndex:
                case ExpressionType.RightShift:
                case ExpressionType.LeftShift:
                case ExpressionType.ExclusiveOr:
                    result = VisitBinary(exp as BinaryExpression);
                    break;
                case ExpressionType.Negate:
                case ExpressionType.NegateChecked:
                case ExpressionType.Not:
                case ExpressionType.Convert:
                case ExpressionType.ConvertChecked:
                case ExpressionType.ArrayLength:
                case ExpressionType.Quote:
                case ExpressionType.TypeAs:
                    result = VisitUnary(exp as UnaryExpression);
                    break;
                case ExpressionType.Parameter:
                    result = VisitParameter(exp as ParameterExpression);
                    break;
                case ExpressionType.Call:
                    result = VisitMethodCall(exp as MethodCallExpression);
                    break;
                case ExpressionType.New:
                    result = VisitNew(exp as NewExpression);
                    break;
                case ExpressionType.NewArrayInit:
                case ExpressionType.NewArrayBounds:
                    result = VisitNewArray(exp as NewArrayExpression);
                    break;
                default:
                    result = exp.ToString();
                    break;
            }

            if (compiledExp != null)
            {
                if (compiledExp.IsCompiled == false)
                {
                    compiledExp.Compile(result);
                }
                return compiledExp.CompiledOutput;
            }

            return result;

        }

        protected virtual string VisitLambda(LambdaExpression lambda)
        {
            if (lambda.Body.NodeType == ExpressionType.MemberAccess)
            {
                var m = lambda.Body as MemberExpression;

                if (m != null && m.Expression != null)
                {
                    //This deals with members that are boolean (i.e. x => IsTrashed )
                    string r = VisitMemberAccess(m);

                    SqlParameters.Add(true);

                    //don't execute if compiled
                    if (IsCompiled == false)
                    {
                        return string.Format("{0} = @{1}", r, SqlParameters.Count - 1);
                    }
                    //already compiled, return
                    return string.Empty;
                }

            }
            return Visit(lambda.Body);
        }

        protected virtual string VisitBinary(BinaryExpression b)
        {
            var left = string.Empty;
            var right = string.Empty;

            var operand = BindOperant(b.NodeType);
            if (operand == "AND" || operand == "OR")
            {
                MemberExpression m = b.Left as MemberExpression;
                if (m != null && m.Expression != null)
                {
                    string r = VisitMemberAccess(m);

                    SqlParameters.Add(1);

                    //don't execute if compiled
                    if (IsCompiled == false)
                    {
                        left = string.Format("{0} = @{1}", r, SqlParameters.Count - 1);
                    }
                }
                else
                {
                    left = Visit(b.Left);
                }
                m = b.Right as MemberExpression;
                if (m != null && m.Expression != null)
                {
                    string r = VisitMemberAccess(m);

                    SqlParameters.Add(1);

                    //don't execute if compiled
                    if (IsCompiled == false)
                    {
                        right = string.Format("{0} = @{1}", r, SqlParameters.Count - 1);
                    }
                }
                else
                {
                    right = Visit(b.Right);
                }
            }
            else if (operand == "=")
            {
                // deal with (x == true|false) - most common
                var constRight = b.Right as ConstantExpression;
                if (constRight != null && constRight.Type == typeof(bool))
                    return ((bool)constRight.Value) ? VisitNotNot(b.Left) : VisitNot(b.Left);
                right = Visit(b.Right);

                // deal with (true|false == x) - why not
                var constLeft = b.Left as ConstantExpression;
                if (constLeft != null && constLeft.Type == typeof(bool))
                    return ((bool)constLeft.Value) ? VisitNotNot(b.Right) : VisitNot(b.Right);
                left = Visit(b.Left);
            }
            else if (operand == "<>")
            {
                // deal with (x != true|false) - most common
                var constRight = b.Right as ConstantExpression;
                if (constRight != null && constRight.Type == typeof(bool))
                    return ((bool)constRight.Value) ? VisitNot(b.Left) : VisitNotNot(b.Left);
                right = Visit(b.Right);

                // deal with (true|false != x) - why not
                var constLeft = b.Left as ConstantExpression;
                if (constLeft != null && constLeft.Type == typeof(bool))
                    return ((bool)constLeft.Value) ? VisitNot(b.Right) : VisitNotNot(b.Right);
                left = Visit(b.Left);
            }
            else
            {
                left = Visit(b.Left);
                right = Visit(b.Right);
            }

            if (operand == "=" && right == "null") operand = "is";
            else if (operand == "<>" && right == "null") operand = "is not";
            else if (operand == "=" || operand == "<>")
            {
                //if (IsTrueExpression(right)) right = GetQuotedTrueValue();
                //else if (IsFalseExpression(right)) right = GetQuotedFalseValue();

                //if (IsTrueExpression(left)) left = GetQuotedTrueValue();
                //else if (IsFalseExpression(left)) left = GetQuotedFalseValue();

            }

            switch (operand)
            {
                case "MOD":
                case "COALESCE":
                    //don't execute if compiled
                    if (IsCompiled == false)
                    {
                        return string.Format("{0}({1},{2})", operand, left, right);
                    }
                    //already compiled, return
                    return string.Empty;
                default:
                    //don't execute if compiled
                    if (IsCompiled == false)
                    {
                        return string.Concat("(", left, " ", operand, " ", right, ")");
                    }
                    //already compiled, return
                    return string.Empty;
            }
        }

        protected virtual List<Object> VisitExpressionList(ReadOnlyCollection<Expression> original)
        {
            var list = new List<Object>();
            for (int i = 0, n = original.Count; i < n; i++)
            {
                if (original[i].NodeType == ExpressionType.NewArrayInit ||
                 original[i].NodeType == ExpressionType.NewArrayBounds)
                {

                    list.AddRange(VisitNewArrayFromExpressionList(original[i] as NewArrayExpression));
                }
                else
                    list.Add(Visit(original[i]));

            }
            return list;
        }

        protected virtual string VisitNew(NewExpression nex)
        {
            // TODO : check !
            var member = Expression.Convert(nex, typeof(object));
            var lambda = Expression.Lambda<Func<object>>(member);
            try
            {
                var getter = lambda.Compile();
                object o = getter();

                SqlParameters.Add(o);

                //don't execute if compiled
                if (IsCompiled == false)
                {
                    return string.Format("@{0}", SqlParameters.Count - 1);
                }
                //already compiled, return
                return string.Empty;
            }
            catch (InvalidOperationException)
            {
                //don't execute if compiled
                if (IsCompiled == false)
                {
                    // FieldName ?
                    List<Object> exprs = VisitExpressionList(nex.Arguments);
                    var r = new StringBuilder();
                    foreach (Object e in exprs)
                    {
                        r.AppendFormat("{0}{1}",
                            r.Length > 0 ? "," : "",
                            e);
                    }
                    return r.ToString();
                }
                //already compiled, return
                return string.Empty;
            }

        }

        protected virtual string VisitParameter(ParameterExpression p)
        {
            return p.Name;
        }

        protected virtual string VisitConstant(ConstantExpression c)
        {
            if (c.Value == null)
                return "null";

            SqlParameters.Add(c.Value);

            //don't execute if compiled
            if (IsCompiled == false)
            {
                return string.Format("@{0}", SqlParameters.Count - 1);
            }
            //already compiled, return
            return string.Empty;
        }

        protected virtual string VisitUnary(UnaryExpression u)
        {
            switch (u.NodeType)
            {
                case ExpressionType.Not:
                    return VisitNot(u.Operand);
                default:
                    return Visit(u.Operand);
            }
        }

        private string VisitNot(Expression exp)
        {
            var o = Visit(exp);

            // use a "NOT (...)" syntax instead of "<>" since we don't know whether "<>" works in all sql servers
            // also, x.StartsWith(...) translates to "x LIKE '...%'" which we cannot "<>" and have to "NOT (...")

            switch (exp.NodeType)
            {
                case ExpressionType.MemberAccess:
                    // false property , i.e. x => !Trashed
                    SqlParameters.Add(true);
                    //don't execute if compiled
                    if (IsCompiled == false)
                    {
                        return string.Format("NOT ({0} = @{1})", o, SqlParameters.Count - 1);
                    }
                    //already compiled, return
                    return string.Empty;
                default:
                    //don't execute if compiled
                    if (IsCompiled == false)
                    {
                        // could be anything else, such as: x => !x.Path.StartsWith("-20")
                        return string.Concat("NOT (", o, ")");
                    }
                    //already compiled, return
                    return string.Empty;
            }
        }

        private string VisitNotNot(Expression exp)
        {
            var o = Visit(exp);

            switch (exp.NodeType)
            {
                case ExpressionType.MemberAccess:
                    // true property, i.e. x => Trashed
                    SqlParameters.Add(true);

                    //don't execute if compiled
                    if (IsCompiled == false)
                    {
                        return string.Format("({0} = @{1})", o, SqlParameters.Count - 1);
                    }
                    //already compiled, return
                    return string.Empty;
                default:
                    // could be anything else, such as: x => x.Path.StartsWith("-20")
                    return o;
            }
        }

        protected virtual string VisitNewArray(NewArrayExpression na)
        {
            List<Object> exprs = VisitExpressionList(na.Expressions);

            //don't execute if compiled
            if (IsCompiled == false)
            {
                var r = new StringBuilder();
                foreach (Object e in exprs)
                {
                    r.Append(r.Length > 0 ? "," + e : e);
                }

                return r.ToString();
            }
            //already compiled, return
            return string.Empty;

        }

        protected virtual List<Object> VisitNewArrayFromExpressionList(NewArrayExpression na)
        {

            List<Object> exprs = VisitExpressionList(na.Expressions);
            return exprs;
        }

        protected virtual string BindOperant(ExpressionType e)
        {

            switch (e)
            {
                case ExpressionType.Equal:
                    return "=";
                case ExpressionType.NotEqual:
                    return "<>";
                case ExpressionType.GreaterThan:
                    return ">";
                case ExpressionType.GreaterThanOrEqual:
                    return ">=";
                case ExpressionType.LessThan:
                    return "<";
                case ExpressionType.LessThanOrEqual:
                    return "<=";
                case ExpressionType.AndAlso:
                    return "AND";
                case ExpressionType.OrElse:
                    return "OR";
                case ExpressionType.Add:
                    return "+";
                case ExpressionType.Subtract:
                    return "-";
                case ExpressionType.Multiply:
                    return "*";
                case ExpressionType.Divide:
                    return "/";
                case ExpressionType.Modulo:
                    return "MOD";
                case ExpressionType.Coalesce:
                    return "COALESCE";
                default:
                    return e.ToString();
            }
        }

        protected virtual string VisitMethodCall(MethodCallExpression m)
        {
            //Here's what happens with a MethodCallExpression:
            //  If a method is called that contains a single argument, 
            //      then m.Object is the object on the left hand side of the method call, example:
            //      x.Path.StartsWith(content.Path)
            //          m.Object = x.Path
            //          and m.Arguments.Length == 1, therefor m.Arguments[0] == content.Path
            //  If a method is called that contains multiple arguments, then m.Object == null and the 
            //      m.Arguments collection contains the left hand side of the method call, example:
            //      x.Path.SqlStartsWith(content.Path, TextColumnType.NVarchar)
            //          m.Object == null
            //          m.Arguments.Length == 3, therefor, m.Arguments[0] == x.Path, m.Arguments[1] == content.Path, m.Arguments[2] == TextColumnType.NVarchar 
            // So, we need to cater for these scenarios.

            var objectForMethod = m.Object ?? m.Arguments[0];
            var visitedObjectForMethod = Visit(objectForMethod);
            var methodArgs = m.Object == null
                ? m.Arguments.Skip(1).ToArray()
                : m.Arguments.ToArray();

            switch (m.Method.Name)
            {
                case "ToString":
                    SqlParameters.Add(objectForMethod.ToString());
                    //don't execute if compiled
                    if (IsCompiled == false)
                        return string.Format("@{0}", SqlParameters.Count - 1);
                    //already compiled, return
                    return string.Empty;
                case "ToUpper":
                    //don't execute if compiled
                    if (IsCompiled == false)
                        return string.Format("upper({0})", visitedObjectForMethod);
                    //already compiled, return
                    return string.Empty;
                case "ToLower":
                    //don't execute if compiled
                    if (IsCompiled == false)
                        return string.Format("lower({0})", visitedObjectForMethod);
                    //already compiled, return
                    return string.Empty;
                case "SqlWildcard":
                case "StartsWith":
                case "EndsWith":
                case "Contains":
                case "Equals":
                case "SqlStartsWith":
                case "SqlEndsWith":
                case "SqlContains":
                case "SqlEquals":
                case "InvariantStartsWith":
                case "InvariantEndsWith":
                case "InvariantContains":
                case "InvariantEquals":

                    string compareValue;

                    if (methodArgs[0].NodeType != ExpressionType.Constant)
                    {
                        //This occurs when we are getting a value from a non constant such as: x => x.Path.StartsWith(content.Path)
                        // So we'll go get the value:
                        var member = Expression.Convert(methodArgs[0], typeof(object));
                        var lambda = Expression.Lambda<Func<object>>(member);
                        var getter = lambda.Compile();
                        compareValue = getter().ToString();
                    }
                    else
                    {
                        compareValue = methodArgs[0].ToString();
                    }

                    //special case, if it is 'Contains' and the member that Contains is being called on is not a string, then
                    // we should be doing an 'In' clause - but we currently do not support this
                    if (methodArgs[0].Type != typeof(string) && TypeHelper.IsTypeAssignableFrom<IEnumerable>(methodArgs[0].Type))
                    {
                        throw new NotSupportedException("An array Contains method is not supported");
                    }

                    //default column type
                    var colType = TextColumnType.NVarchar;

                    //then check if the col type argument has been passed to the current method (this will be the case for methods like
                    // SqlContains and other Sql methods)
                    if (methodArgs.Length > 1)
                    {
                        var colTypeArg = methodArgs.FirstOrDefault(x => x is ConstantExpression && x.Type == typeof(TextColumnType));
                        if (colTypeArg != null)
                        {
                            colType = (TextColumnType)((ConstantExpression)colTypeArg).Value;
                        }
                    }

                    return HandleStringComparison(visitedObjectForMethod, compareValue, m.Method.Name, colType);

                case "Replace":
                    string searchValue;

                    if (methodArgs[0].NodeType != ExpressionType.Constant)
                    {
                        //This occurs when we are getting a value from a non constant such as: x => x.Path.StartsWith(content.Path)
                        // So we'll go get the value:
                        var member = Expression.Convert(methodArgs[0], typeof(object));
                        var lambda = Expression.Lambda<Func<object>>(member);
                        var getter = lambda.Compile();
                        searchValue = getter().ToString();
                    }
                    else
                    {
                        searchValue = methodArgs[0].ToString();
                    }

                    if (methodArgs[0].Type != typeof(string) && TypeHelper.IsTypeAssignableFrom<IEnumerable>(methodArgs[0].Type))
                    {
                        throw new NotSupportedException("An array Contains method is not supported");
                    }

                    string replaceValue;

                    if (methodArgs[1].NodeType != ExpressionType.Constant)
                    {
                        //This occurs when we are getting a value from a non constant such as: x => x.Path.StartsWith(content.Path)
                        // So we'll go get the value:
                        var member = Expression.Convert(methodArgs[1], typeof(object));
                        var lambda = Expression.Lambda<Func<object>>(member);
                        var getter = lambda.Compile();
                        replaceValue = getter().ToString();
                    }
                    else
                    {
                        replaceValue = methodArgs[1].ToString();
                    }

                    if (methodArgs[1].Type != typeof(string) && TypeHelper.IsTypeAssignableFrom<IEnumerable>(methodArgs[1].Type))
                    {
                        throw new NotSupportedException("An array Contains method is not supported");
                    }

                    SqlParameters.Add(RemoveQuote(searchValue));

                    SqlParameters.Add(RemoveQuote(replaceValue));

                    //don't execute if compiled
                    if (IsCompiled == false)
                        return string.Format("replace({0}, @{1}, @{2})", visitedObjectForMethod, SqlParameters.Count - 2, SqlParameters.Count - 1);
                    //already compiled, return
                    return string.Empty;

                //case "Substring":
                //    var startIndex = Int32.Parse(args[0].ToString()) + 1;
                //    if (args.Count == 2)
                //    {
                //        var length = Int32.Parse(args[1].ToString());
                //        return string.Format("substring({0} from {1} for {2})",
                //                         r,
                //                         startIndex,
                //                         length);
                //    }
                //    else
                //        return string.Format("substring({0} from {1})",
                //                         r,
                //                         startIndex);
                //case "Round":
                //case "Floor":
                //case "Ceiling":
                //case "Coalesce":
                //case "Abs":
                //case "Sum":
                //    return string.Format("{0}({1}{2})",
                //                         m.Method.Name,
                //                         r,
                //                         args.Count == 1 ? string.Format(",{0}", args[0]) : "");
                //case "Concat":
                //    var s = new StringBuilder();
                //    foreach (Object e in args)
                //    {
                //        s.AppendFormat(" || {0}", e);
                //    }
                //    return string.Format("{0}{1}", r, s);

                //case "In":

                //    var member = Expression.Convert(m.Arguments[0], typeof(object));
                //    var lambda = Expression.Lambda<Func<object>>(member);
                //    var getter = lambda.Compile();

                //    var inArgs = (object[])getter();

                //    var sIn = new StringBuilder();
                //    foreach (var e in inArgs)
                //    {
                //        SqlParameters.Add(e);

                //        sIn.AppendFormat("{0}{1}",
                //                     sIn.Length > 0 ? "," : "",
                //                                    string.Format("@{0}", SqlParameters.Count - 1));

                //        //sIn.AppendFormat("{0}{1}",
                //        //             sIn.Length > 0 ? "," : "",
                //        //                            GetQuotedValue(e, e.GetType()));
                //    }

                //    return string.Format("{0} {1} ({2})", r, m.Method.Name, sIn.ToString());
                //case "Desc":
                //    return string.Format("{0} DESC", r);
                //case "Alias":
                //case "As":
                //    return string.Format("{0} As {1}", r,
                //                                GetQuotedColumnName(RemoveQuoteFromAlias(RemoveQuote(args[0].ToString()))));

                default:

                    throw new ArgumentOutOfRangeException("No logic supported for " + m.Method.Name);

                    //var s2 = new StringBuilder();
                    //foreach (Object e in args)
                    //{
                    //    s2.AppendFormat(",{0}", GetQuotedValue(e, e.GetType()));
                    //}
                    //return string.Format("{0}({1}{2})", m.Method.Name, r, s2.ToString());
            }
        }

        public virtual string GetQuotedTableName(string tableName)
        {
            //already compiled, return
            if (IsCompiled)
                return tableName;

            return string.Format("\"{0}\"", tableName);
        }

        public virtual string GetQuotedColumnName(string columnName)
        {
            //already compiled, return
            if (IsCompiled)
                return columnName;

            return string.Format("\"{0}\"", columnName);
        }

        public virtual string GetQuotedName(string name)
        {
            //already compiled, return
            if (IsCompiled)
                return name;

            return string.Format("\"{0}\"", name);
        }

        public object[] GetSqlParameters()
        {
            return SqlParameters.ToArray();
        }

        protected string HandleStringComparison(string col, string val, string verb, TextColumnType columnType)
        {
            switch (verb)
            {
                case "SqlWildcard":
                    SqlParameters.Add(RemoveQuote(val));
                    //don't execute if compiled
                    if (IsCompiled == false)
                        return SqlSyntax.GetStringColumnWildcardComparison(col, SqlParameters.Count - 1, columnType);
                    //already compiled, return
                    return string.Empty;
                case "Equals":
                    SqlParameters.Add(RemoveQuote(val));
                    //don't execute if compiled
                    if (IsCompiled == false)
                        return SqlSyntax.GetStringColumnEqualComparison(col, SqlParameters.Count - 1, columnType);
                    //already compiled, return
                    return string.Empty;
                case "StartsWith":
                    SqlParameters.Add(string.Format("{0}{1}",
                        RemoveQuote(val),
                        SqlSyntax.GetWildcardPlaceholder()));
                    //don't execute if compiled
                    if (IsCompiled == false)
                        return SqlSyntax.GetStringColumnWildcardComparison(col, SqlParameters.Count - 1, columnType);
                    //already compiled, return
                    return string.Empty;
                case "EndsWith":
                    SqlParameters.Add(string.Format("{0}{1}",
                        SqlSyntax.GetWildcardPlaceholder(),
                        RemoveQuote(val)));
                    //don't execute if compiled
                    if (IsCompiled == false)
                        return SqlSyntax.GetStringColumnWildcardComparison(col, SqlParameters.Count - 1, columnType);
                    //already compiled, return
                    return string.Empty;
                case "Contains":
                    SqlParameters.Add(string.Format("{0}{1}{0}",
                        SqlSyntax.GetWildcardPlaceholder(),
                        RemoveQuote(val)));
                    //don't execute if compiled
                    if (IsCompiled == false)
                        return SqlSyntax.GetStringColumnWildcardComparison(col, SqlParameters.Count - 1, columnType);
                    //already compiled, return
                    return string.Empty;
                case "InvariantEquals":
                case "SqlEquals":
                    //recurse
                    return HandleStringComparison(col, val, "Equals", columnType);
                case "InvariantStartsWith":
                case "SqlStartsWith":
                    //recurse
                    return HandleStringComparison(col, val, "StartsWith", columnType);
                case "InvariantEndsWith":
                case "SqlEndsWith":
                    //recurse
                    return HandleStringComparison(col, val, "EndsWith", columnType);
                case "InvariantContains":
                case "SqlContains":
                    //recurse
                    return HandleStringComparison(col, val, "Contains", columnType);
                default:
                    throw new ArgumentOutOfRangeException("verb");
            }
        }

        //public virtual string GetQuotedValue(object value, Type fieldType, Func<object, string> escapeCallback = null, Func<Type, bool> shouldQuoteCallback = null)
        //{
        //    if (value == null) return "NULL";

        //    if (escapeCallback == null)
        //    {
        //        escapeCallback = EscapeParam;
        //    }
        //    if (shouldQuoteCallback == null)
        //    {
        //        shouldQuoteCallback = ShouldQuoteValue;
        //    }

        //    if (!fieldType.UnderlyingSystemType.IsValueType && fieldType != typeof(string))
        //    {
        //        //if (TypeSerializer.CanCreateFromString(fieldType))
        //        //{
        //        //    return "'" + escapeCallback(TypeSerializer.SerializeToString(value)) + "'";
        //        //}

        //        throw new NotSupportedException(
        //            string.Format("Property of type: {0} is not supported", fieldType.FullName));
        //    }

        //    if (fieldType == typeof(int))
        //        return ((int)value).ToString(CultureInfo.InvariantCulture);

        //    if (fieldType == typeof(float))
        //        return ((float)value).ToString(CultureInfo.InvariantCulture);

        //    if (fieldType == typeof(double))
        //        return ((double)value).ToString(CultureInfo.InvariantCulture);

        //    if (fieldType == typeof(decimal))
        //        return ((decimal)value).ToString(CultureInfo.InvariantCulture);

        //    if (fieldType == typeof(DateTime))
        //    {
        //        return "'" + escapeCallback(((DateTime)value).ToIsoString()) + "'";
        //    }

        //    if (fieldType == typeof(bool))
        //        return ((bool)value) ? Convert.ToString(1, CultureInfo.InvariantCulture) : Convert.ToString(0, CultureInfo.InvariantCulture);

        //    return shouldQuoteCallback(fieldType)
        //               ? "'" + escapeCallback(value) + "'"
        //               : value.ToString();
        //}

        public virtual string EscapeParam(object paramValue)
        {
            return paramValue == null 
                ? string.Empty 
                : SqlSyntax.EscapeString(paramValue.ToString());
        }
        
        public virtual bool ShouldQuoteValue(Type fieldType)
        {
            return true;
        }

        protected virtual string RemoveQuote(string exp)
        {
            if ((exp.StartsWith("\"") || exp.StartsWith("`") || exp.StartsWith("'"))
                    &&
                    (exp.EndsWith("\"") || exp.EndsWith("`") || exp.EndsWith("'")))
            {
                exp = exp.Remove(0, 1);
                exp = exp.Remove(exp.Length - 1, 1);
            }
            return exp;
        }

        //protected virtual string RemoveQuoteFromAlias(string exp)
        //{

        //    if ((exp.StartsWith("\"") || exp.StartsWith("`") || exp.StartsWith("'"))
        //        &&
        //        (exp.EndsWith("\"") || exp.EndsWith("`") || exp.EndsWith("'")))
        //    {
        //        exp = exp.Remove(0, 1);
        //        exp = exp.Remove(exp.Length - 1, 1);
        //    }
        //    return exp;
        //}
    }
}