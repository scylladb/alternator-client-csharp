// <copyright file="KeyAffinityRequestClassifier.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator.KeyRouting
{
    using System.Text;
    using Amazon.DynamoDBv2;
    using Amazon.DynamoDBv2.Model;
    using Amazon.Runtime;

    public static class KeyAffinityRequestClassifier
    {
        public static AttributeValue? ExtractPartitionKey(AmazonWebServiceRequest request, string? pkAttributeName)
        {
            if (pkAttributeName == null)
            {
                return null;
            }

            IDictionary<string, AttributeValue>? key = null;
            if (request is UpdateItemRequest updateItem)
            {
                key = updateItem.Key;
            }
            else if (request is PutItemRequest putItem)
            {
                key = putItem.Item;
            }
            else if (request is DeleteItemRequest deleteItem)
            {
                key = deleteItem.Key;
            }
            else if (request is GetItemRequest getItem)
            {
                key = getItem.Key;
            }
            else if (request is BatchWriteItemRequest batchWriteItem)
            {
                foreach (var target in ExtractBatchWriteRoutingTargets(batchWriteItem))
                {
                    var partitionKey = target.PartitionKeyValue(pkAttributeName);
                    if (partitionKey != null)
                    {
                        return partitionKey;
                    }
                }
            }

            if (key != null && key.TryGetValue(pkAttributeName, out var value))
            {
                return value;
            }

            return null;
        }

        public static string? ExtractTableName(AmazonWebServiceRequest request)
        {
            return request switch
            {
                UpdateItemRequest updateItem => updateItem.TableName,
                PutItemRequest putItem => putItem.TableName,
                DeleteItemRequest deleteItem => deleteItem.TableName,
                GetItemRequest getItem => getItem.TableName,
                QueryRequest query => query.TableName,
                BatchWriteItemRequest batchWriteItem => FindBatchWriteRoutingTarget(batchWriteItem)?.TableName,
                _ => null,
            };
        }

        public static bool ShouldApply(KeyRouteAffinity mode, AmazonWebServiceRequest request)
        {
            if (mode == KeyRouteAffinity.None)
            {
                return false;
            }

            return request switch
            {
                UpdateItemRequest updateItem => ShouldApplyUpdateItem(mode, updateItem),
                PutItemRequest putItem => ShouldApplyPutItem(mode, putItem),
                DeleteItemRequest deleteItem => ShouldApplyDeleteItem(mode, deleteItem),
                BatchWriteItemRequest batchWriteItem => ShouldApplyBatchWriteItem(mode, batchWriteItem),
                _ => false,
            };
        }

        public static IReadOnlyList<BatchWriteRoutingTarget> ExtractBatchWriteRoutingTargets(BatchWriteItemRequest request)
        {
            if (request.RequestItems == null || request.RequestItems.Count == 0)
            {
                return Array.Empty<BatchWriteRoutingTarget>();
            }

            var candidates = new List<BatchWriteRoutingTarget>();
            foreach (var table in request.RequestItems)
            {
                if (table.Value == null)
                {
                    continue;
                }

                foreach (var write in table.Value)
                {
                    if (write.PutRequest != null)
                    {
                        candidates.Add(new BatchWriteRoutingTarget(
                            table.Key,
                            write.PutRequest.Item,
                            "PutRequest",
                            CanonicalAttributeValues(write.PutRequest.Item)));
                    }

                    if (write.DeleteRequest != null)
                    {
                        candidates.Add(new BatchWriteRoutingTarget(
                            table.Key,
                            write.DeleteRequest.Key,
                            "DeleteRequest",
                            CanonicalAttributeValues(write.DeleteRequest.Key)));
                    }
                }
            }

            return candidates
                .OrderBy(target => target.TableName, StringComparer.Ordinal)
                .ThenBy(target => target.CanonicalAttributes, StringComparer.Ordinal)
                .ThenBy(target => target.Operation, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly();
        }

#pragma warning disable SA1300, IDE1006
        public static AttributeValue? extractPartitionKey(AmazonWebServiceRequest request, string? pkAttributeName)
        {
            return ExtractPartitionKey(request, pkAttributeName);
        }

        public static string? extractTableName(AmazonWebServiceRequest request)
        {
            return ExtractTableName(request);
        }

        public static bool shouldApply(KeyRouteAffinity mode, AmazonWebServiceRequest request)
        {
            return ShouldApply(mode, request);
        }

        public static IReadOnlyList<BatchWriteRoutingTarget> extractBatchWriteRoutingTargets(BatchWriteItemRequest request)
        {
            return ExtractBatchWriteRoutingTargets(request);
        }
#pragma warning restore SA1300, IDE1006

        private static string CanonicalAttributeValues(IDictionary<string, AttributeValue>? values)
        {
            if (values == null || values.Count == 0)
            {
                return "{}";
            }

            var builder = new StringBuilder();
            builder.Append('{');
            var first = true;
            foreach (var key in values.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                AppendQuoted(builder, key);
                builder.Append(':');
                AppendCanonicalAttributeValue(builder, values[key]);
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static void AppendCanonicalAttributeValue(StringBuilder builder, AttributeValue? value)
        {
            if (value == null)
            {
                builder.Append("{\"UNKNOWN\":true}");
                return;
            }

            if (value.S != null)
            {
                AppendTaggedJsonString(builder, "S", value.S);
                return;
            }

            if (value.N != null)
            {
                AppendTaggedJsonString(builder, "N", value.N);
                return;
            }

            if (value.B != null)
            {
                builder.Append("{\"B\":{\"__bytes__\":\"");
                AppendHex(builder, value.B.ToArray());
                builder.Append("\"}}");
                return;
            }

            if (value.IsBOOLSet)
            {
                builder.Append("{\"BOOL\":").Append(value.BOOL == true ? "true" : "false").Append('}');
                return;
            }

            if (value.NULL == true)
            {
                builder.Append("{\"NULL\":true}");
                return;
            }

            if (value.IsSSSet)
            {
                AppendTaggedJsonStringList(builder, "SS", value.SS);
                return;
            }

            if (value.IsNSSet)
            {
                AppendTaggedJsonStringList(builder, "NS", value.NS);
                return;
            }

            if (value.IsBSSet)
            {
                builder.Append("{\"BS\":[");
                for (var index = 0; index < value.BS.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append("{\"__bytes__\":\"");
                    AppendHex(builder, value.BS[index].ToArray());
                    builder.Append("\"}");
                }

                builder.Append("]}");
                return;
            }

            if (value.IsLSet)
            {
                builder.Append("{\"L\":[");
                for (var index = 0; index < value.L.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    AppendCanonicalAttributeValue(builder, value.L[index]);
                }

                builder.Append("]}");
                return;
            }

            if (value.IsMSet)
            {
                builder.Append("{\"M\":");
                builder.Append(CanonicalAttributeValues(value.M));
                builder.Append('}');
                return;
            }

            builder.Append("{\"UNKNOWN\":true}");
        }

        private static void AppendHex(StringBuilder builder, byte[] bytes)
        {
            const string Hex = "0123456789abcdef";
            foreach (var value in bytes)
            {
                builder.Append(Hex[(value >> 4) & 0x0f]);
                builder.Append(Hex[value & 0x0f]);
            }
        }

        private static void AppendHex4(StringBuilder builder, char value)
        {
            const string Hex = "0123456789abcdef";
            builder.Append(Hex[(value >> 12) & 0x0f]);
            builder.Append(Hex[(value >> 8) & 0x0f]);
            builder.Append(Hex[(value >> 4) & 0x0f]);
            builder.Append(Hex[value & 0x0f]);
        }

        private static void AppendQuoted(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character <= 0x1f)
                        {
                            builder.Append("\\u");
                            AppendHex4(builder, character);
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        private static void AppendTaggedJsonString(StringBuilder builder, string tag, string value)
        {
            builder.Append("{\"");
            builder.Append(tag);
            builder.Append("\":");
            AppendQuoted(builder, value);
            builder.Append('}');
        }

        private static void AppendTaggedJsonStringList(StringBuilder builder, string tag, IList<string> values)
        {
            builder.Append("{\"");
            builder.Append(tag);
            builder.Append("\":[");
            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                AppendQuoted(builder, values[index]);
            }

            builder.Append("]}");
        }

        private static BatchWriteRoutingTarget? FindBatchWriteRoutingTarget(BatchWriteItemRequest request)
        {
            return ExtractBatchWriteRoutingTargets(request).FirstOrDefault();
        }

        private static bool HasExpected<TKey, TValue>(IDictionary<TKey, TValue>? values)
        {
            return values != null && values.Count != 0;
        }

        private static bool IsNotNone(ReturnValue? returnValue)
        {
            return returnValue != null && returnValue != ReturnValue.NONE;
        }

        private static bool ShouldApplyBatchWriteItem(KeyRouteAffinity mode, BatchWriteItemRequest request)
        {
            return mode == KeyRouteAffinity.AnyWrite
                && ExtractBatchWriteRoutingTargets(request).Count != 0;
        }

        private static bool ShouldApplyDeleteItem(KeyRouteAffinity mode, DeleteItemRequest request)
        {
            return mode == KeyRouteAffinity.AnyWrite
                || !string.IsNullOrEmpty(request.ConditionExpression)
                || HasExpected(request.Expected)
                || IsNotNone(request.ReturnValues);
        }

        private static bool ShouldApplyPutItem(KeyRouteAffinity mode, PutItemRequest request)
        {
            return mode == KeyRouteAffinity.AnyWrite
                || !string.IsNullOrEmpty(request.ConditionExpression)
                || HasExpected(request.Expected)
                || IsNotNone(request.ReturnValues);
        }

        private static bool ShouldApplyUpdateItem(KeyRouteAffinity mode, UpdateItemRequest request)
        {
            if (mode == KeyRouteAffinity.AnyWrite
                || !string.IsNullOrEmpty(request.UpdateExpression)
                || !string.IsNullOrEmpty(request.ConditionExpression)
                || HasExpected(request.Expected)
                || request.ReturnValues == ReturnValue.ALL_OLD
                || request.ReturnValues == ReturnValue.UPDATED_OLD
                || request.ReturnValues == ReturnValue.ALL_NEW)
            {
                return true;
            }

            if (request.AttributeUpdates == null)
            {
                return false;
            }

            return request.AttributeUpdates.Values.Any(update =>
                update.Action == AttributeAction.ADD
                || (update.Action == AttributeAction.DELETE && update.Value != null));
        }

        public sealed class BatchWriteRoutingTarget
        {
            internal BatchWriteRoutingTarget(
                string tableName,
                IDictionary<string, AttributeValue>? values,
                string operation,
                string canonicalAttributes)
            {
                this.TableName = tableName;
                this.Values = values;
                this.Operation = operation;
                this.CanonicalAttributes = canonicalAttributes;
            }

            public string TableName { get; }

            public IDictionary<string, AttributeValue>? Values { get; }

            public string Operation { get; }

            public string CanonicalAttributes { get; }

            public AttributeValue? PartitionKeyValue(string? pkAttributeName)
            {
                if (this.Values == null || pkAttributeName == null)
                {
                    return null;
                }

                return this.Values.TryGetValue(pkAttributeName, out var value) ? value : null;
            }

#pragma warning disable SA1300, IDE1006
            public string tableName()
            {
                return this.TableName;
            }

            public IDictionary<string, AttributeValue>? values()
            {
                return this.Values;
            }

            public string operation()
            {
                return this.Operation;
            }

            public string canonicalAttributes()
            {
                return this.CanonicalAttributes;
            }

            public AttributeValue? partitionKeyValue(string? pkAttributeName)
            {
                return this.PartitionKeyValue(pkAttributeName);
            }
#pragma warning restore SA1300, IDE1006
        }
    }
}
