﻿using System;
using System.Linq;
using Newtonsoft.Json;
using OrderCloud.Catalyst;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;
using Headstart.Common.Exceptions;

namespace Headstart.Common.Models
{
	public class WorkItem
	{
		public string ResourceId { get; set; } = string.Empty;

		public string RecordId { get; set; } = string.Empty;

		[JsonConverter(typeof(StringEnumConverter))]
		public RecordType RecordType { get; set; }

		[JsonConverter(typeof(StringEnumConverter))]
		public Action Action { get; set; }

		public JObject Current { get; set; } = new JObject(); // Not used for delete

		public JObject Cache { get; set; } = new JObject(); // Not used for create

		public JObject Diff { get; set; } = new JObject();

		public string Token { get; set; } = string.Empty;

		public string ClientId { get; set; } = string.Empty;

		public DecodedToken User { get; set; } = new DecodedToken();

		public WorkItem()
		{
		}

		public WorkItem(string path)
		{
			string[] split = path.Split("/");
			ResourceId = split[0];
			RecordId = split[split.Length - 1].Replace($@".json", "");
			RecordType = split[2] switch
			{
				@"templateproductflat" => RecordType.TemplateProductFlat,
				@"hydratedproduct" => RecordType.HydratedProduct,
				@"product" => RecordType.Product,
				@"priceschedule" => RecordType.PriceSchedule,
				@"productfacet" => RecordType.ProductFacet,
				@"specproductassignment" => RecordType.SpecProductAssignment,
				@"specoption" => RecordType.SpecOption,
				@"spec" => RecordType.Spec,
				@"buyer" => RecordType.Buyer,
				@"user" => RecordType.User,
				@"usergroup" => RecordType.UserGroup,
				@"address" => RecordType.Address,
				@"usergroupassignment" => RecordType.UserGroupAssignment,
				@"addressassignment" => RecordType.AddressAssignment,
				@"costcenter" => RecordType.CostCenter,
				@"catalogassignment" => RecordType.CatalogAssignment,
				@"catalog" => RecordType.Catalog,
				_ => throw new OrchestrationException(OrchestrationErrorType.WorkItemDefinition, path)
			};
		}
	}

	[JsonConverter(typeof(StringEnumConverter))]
	public enum RecordType
	{
		HydratedProduct, 
		Product, 
		PriceSchedule, 
		Spec, 
		SpecOption, 
		SpecProductAssignment, 
		ProductFacet,
		Buyer,
		User, 
		UserGroup, 
		Address, 
		CostCenter, 
		UserGroupAssignment, 
		AddressAssignment,
		CatalogAssignment, 
		Catalog, 
		Supplier, 
		Order, 
		TemplateProductFlat
	}

	[JsonConverter(typeof(StringEnumConverter))]
	public enum Action
	{
		Ignore, 
		Create, 
		Update, 
		Patch, 
		Delete, 
		Get, 
		SyncShipments
	}

	public static class WorkItemMethods
	{
		public static Action DetermineAction(WorkItem wi)
		{
			// we want to ensure a condition is met before determining an action
			// so that if there is a case not compensated for it flows to an exception
			try
			{
				// first check if there is a cache object, if not it's a CREATE event
				if (wi.Cache == null)
				{
					return Action.Create;
				}

				// if cache does exists, and there is no difference ignore the action
				if (wi.Cache != null && wi.Diff == null)
				{
					return (wi.RecordType == RecordType.SpecProductAssignment || wi.RecordType == RecordType.UserGroupAssignment || wi.RecordType == RecordType.CatalogAssignment)
						? Action.Ignore : Action.Get;
				}

				// special case for SpecAssignment because there is no ID for the OC object
				// but we want one in orchestration to handle caching
				// in further retrospect I don't think we need to handle updating objects when only the ID is being changed
				// maybe in the future a true business case will be necessary to do this
				if ((wi.RecordType == RecordType.SpecProductAssignment || wi.RecordType == RecordType.UserGroupAssignment || wi.RecordType == RecordType.CatalogAssignment)
				    && wi.Diff.Children().Count() == 1 && wi.Diff.SelectToken("ID").Path.Equals("ID", StringComparison.OrdinalIgnoreCase))
				{
					return Action.Ignore;
				}

				if (wi.Cache != null && wi.Diff != null)
				{
					// cache exists, we want to force a PUT when xp has deleted properties because 
					// it's the only way to delete the properties
					// otherwise we want to PATCH the existing object
					//TODO: figure this reference out
					//return wi.Cache.HasDeletedXp(wi.Current) ? Action.Update : Action.Patch;
					return Action.Patch;
				}
			}
			catch (Exception ex)
			{
				throw new OrchestrationException(OrchestrationErrorType.ActionEvaluationError, wi, ex.Message);
			}

			throw new OrchestrationException(OrchestrationErrorType.ActionEvaluationError, wi, @"Unable to determine action.");
		}
	}
}