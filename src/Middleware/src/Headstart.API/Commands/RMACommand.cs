﻿using System;
using System.Linq;
using OrderCloud.SDK;
using Headstart.Models;
using Headstart.Common;
using OrderCloud.Catalyst;
using Sitecore.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Headstart.Common.Models;
using Headstart.Models.Extended;
using Headstart.Common.Services;
using System.Collections.Generic;
using Headstart.Models.Headstart;
using Headstart.Common.Repositories;
using ordercloud.integrations.library;
using ordercloud.integrations.cardconnect;
using Sitecore.Foundation.SitecoreExtensions.Extensions;
using Headstart.Common.Services.ShippingIntegration.Models;
using Sitecore.Foundation.SitecoreExtensions.MVC.Extensions;

namespace Headstart.API.Commands
{
    public interface IRMACommand
    {
        Task BuildRMA(HSOrder order, List<string> supplierIDs, LineItemStatusChanges lineItemStatusChanges, List<HSLineItem> lineItemsChanged, DecodedToken decodedToken);
        Task<RMA> PostRMA(RMA rma);
        Task<CosmosListPage<RMA>> ListBuyerRMAs(CosmosListOptions listOptions, string buyerID);
        Task<RMA> Get(ListArgs<RMA> args, DecodedToken decodedToken);
        Task<CosmosListPage<RMA>> ListRMAsByOrderID(string orderID, CommerceRole commerceRole, MeUser me, bool accessAllRMAsOnOrder = false);
        Task<CosmosListPage<RMA>> ListRMAs(CosmosListOptions listOptions, DecodedToken decodedToken);
        Task<RMAWithLineItemStatusByQuantity> ProcessRMA(RMA rma, DecodedToken decodedToken);
        Task<RMAWithLineItemStatusByQuantity> ProcessRefund(string rmaNumber, DecodedToken decodedToken);
    }

    public class RMACommand : IRMACommand
    {
        private const string QUEUED_FOR_CAPTURE = "Queued for Capture";
        private readonly IOrderCloudClient _oc;
        private readonly IRMARepo _rmaRepo;
        private readonly IOrderCloudIntegrationsCardConnectService _cardConnect;
        private readonly ISendgridService _sendgridService;
        private readonly AppSettings _settings;
        private readonly WebConfigSettings _webConfigSettings = WebConfigSettings.Instance;

        /// <summary>
        /// The IOC based constructor method for the RMACommand class object with Dependency Injection
        /// </summary>
        /// <param name="oc"></param>
        /// <param name="rmaRepo"></param>
        /// <param name="cardConnect"></param>
        /// <param name="sendgridService"></param>
        /// <param name="settings"></param>
        public RMACommand(IOrderCloudClient oc, IRMARepo rmaRepo, IOrderCloudIntegrationsCardConnectService cardConnect, ISendgridService sendgridService, AppSettings settings)
        {
            try
            {
                _oc = oc;
                _rmaRepo = rmaRepo;
                _cardConnect = cardConnect;
                _sendgridService = sendgridService;
                _settings = settings;
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
        }

        /// <summary>
        /// Public re-usable BuildRMA task method
        /// </summary>
        /// <param name="order"></param>
        /// <param name="supplierIDs"></param>
        /// <param name="lineItemStatusChanges"></param>
        /// <param name="lineItemsChanged"></param>
        /// <param name="decodedToken"></param>
        /// <returns></returns>
        public async Task BuildRMA(HSOrder order, List<string> supplierIDs, LineItemStatusChanges lineItemStatusChanges, List<HSLineItem> lineItemsChanged, DecodedToken decodedToken)
        {
            try
            {
                foreach (string supplierID in supplierIDs)
                {
                    var sellerID = supplierID;
                    var sellerName = supplierID == null ? _settings.OrderCloudSettings.MarketplaceName : (await _oc.Suppliers.GetAsync<HSSupplier>(supplierID)).Name;

                    RMA rma = new RMA()
                    {
                        PartitionKey = $@"PartitionValue",
                        SourceOrderID = order.ID,
                        TotalCredited = 0M,
                        ShippingCredited = 0M,
                        RMANumber = await BuildRMANumber(order),
                        SupplierID = sellerID,
                        SupplierName = sellerName,
                        Type = lineItemStatusChanges.Status == LineItemStatus.CancelRequested ? RMAType.Cancellation : RMAType.Return,
                        DateCreated = DateTime.Now,
                        DateComplete = null,
                        Status = RMAStatus.Requested,
                        LineItems = BuildLineItemRMA(supplierID, lineItemStatusChanges, lineItemsChanged, order),
                        Logs = new List<RMALog>(),
                        FromBuyerID = order.FromCompanyID,
                        FromBuyerUserID = order.FromUser.ID
                    };
                    await PostRMA(rma);
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }    
        }

        /// <summary>
        /// Private re-usable BuildRMANumber task method
        /// </summary>
        /// <param name="order"></param>
        /// <returns>The RMANumber string value from the BuildRMANumber process</returns>
        private async Task<string> BuildRMANumber(HSOrder order)
        {
            var rmaNumber = string.Empty;
            try
            {
                var args = new CosmosListOptions()
                {
                    PageSize = 100,
                    Filters = new List<ListFilter>() { new ListFilter($@"SourceOrderID", order.ID) }
                };

                CosmosListPage<RMA> existingRMAsOnOrder = await ListBuyerRMAs(args, order.FromCompanyID);
                int lastRMANumber = existingRMAsOnOrder.Items.OrderBy(rma => rma.RMANumber).Select(rma => int.Parse(rma.RMANumber.Substring(rma.RMANumber.LastIndexOf($@"-") + 1))).LastOrDefault();
                string rmaSuffix = $@"{lastRMANumber + 1}".PadLeft(2, '0');
                rmaNumber = $@"{order.ID}-RMA-{rmaSuffix}";
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return rmaNumber;
        }

        /// <summary>
        /// Private re-usable BuildLineItemRMA method
        /// </summary>
        /// <param name="supplierID"></param>
        /// <param name="lineItemStatusChanges"></param>
        /// <param name="lineItemsChanged"></param>
        /// <param name="order"></param>
        /// <returns>The list of RMALineItem response objects from the BuildLineItemRMA process</returns>
        private List<RMALineItem> BuildLineItemRMA(string supplierID, LineItemStatusChanges lineItemStatusChanges, List<HSLineItem> lineItemsChanged, HSOrder order)
        {
            var rmaLineItems = new List<RMALineItem>();
            try
            {
                foreach (HSLineItem lineItem in lineItemsChanged)
                {
                    if (lineItem.SupplierID == supplierID)
                    {
                        RMALineItem rmaLineItem = new RMALineItem()
                        {
                            ID = lineItem.ID,
                            QuantityRequested = (int)lineItemStatusChanges.Changes.FirstOrDefault(change => change.ID == lineItem.ID)?.Quantity,
                            QuantityProcessed = 0,
                            Status = RMALineItemStatus.Requested,
                            Reason = lineItemStatusChanges.Changes.FirstOrDefault(change => change.ID == lineItem.ID)?.Reason,
                            PercentToRefund = null,
                            RefundableViaCreditCard = (order.xp.PaymentMethod.Equals($@"Credit Card", StringComparison.OrdinalIgnoreCase)),
                            IsResolved = false,
                            IsRefunded = false,
                            LineTotalRefund = 0
                        };
                        rmaLineItems.Add(rmaLineItem);
                    }
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return rmaLineItems;
        }

        /// <summary>
        /// Public re-usable PostRMA task method
        /// </summary>
        /// <param name="rma"></param>
        /// <returns>The RMA response object from the PostRMA process</returns>
        public virtual async Task<RMA> PostRMA(RMA rma)
        {
            var resp = new RMA(); 
            try
            {
                resp = await _rmaRepo.AddItemAsync(rma);
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return resp;
        }

        /// <summary>
        /// Public re-usable ListBuyerRMAs task method
        /// </summary>
        /// <param name="listOptions"></param>
        /// <param name="buyerID"></param>
        /// <returns>The CosmosListPage of RMA response objects from the ListBuyerRMAs process</returns>
        public async Task<CosmosListPage<RMA>> ListBuyerRMAs(CosmosListOptions listOptions, string buyerID)
        {
            var rmas = new CosmosListPage<RMA>();
            try
            {
                IQueryable<RMA> queryable = _rmaRepo.GetQueryable().Where(rma => rma.FromBuyerID == buyerID);
                rmas = await GenerateRMAList(queryable, listOptions);
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return rmas;
        }

        /// <summary>
        /// Public re-usable Get task method to get the RMA object
        /// </summary>
        /// <param name="args"></param>
        /// <param name="decodedToken"></param>
        /// <returns>The RMA response object from the ListArgs of RMA response objects</returns>
        public async Task<RMA> Get(ListArgs<RMA> args, DecodedToken decodedToken)
        {
            var resp = new RMA();
            try
            {
                CosmosListOptions listOptions = new CosmosListOptions()
                {
                    PageSize = 100,
                    Search = args.Search,
                    SearchOn = $@"RMANumber"
                };

                IQueryable<RMA> queryable = _rmaRepo.GetQueryable().Where(rma => rma.PartitionKey.Equals($@"PartitionValue", StringComparison.OrdinalIgnoreCase));
                if (decodedToken.CommerceRole == CommerceRole.Supplier)
                {
                    var me = await _oc.Me.GetAsync(accessToken: decodedToken.AccessToken);
                    queryable = queryable.Where(rma => rma.SupplierID == me.Supplier.ID);
                }
                CosmosListPage<RMA> rmas = await GenerateRMAList(queryable, listOptions);
                resp = rmas.Items[0];
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return resp;
        }

        /// <summary>
        /// Private re-usable GenerateRMAList task method
        /// </summary>
        /// <param name="queryable"></param>
        /// <param name="listOptions"></param>
        /// <returns>The CosmosListPage of RMA response objects from the GenerateRMAList process</returns>
        private async Task<CosmosListPage<RMA>> GenerateRMAList(IQueryable<RMA> queryable, CosmosListOptions listOptions)
        {
            var rmas = new CosmosListPage<RMA>();
            try
            {
                QueryRequestOptions requestOptions = new QueryRequestOptions();
                requestOptions.MaxItemCount = listOptions.PageSize;
                rmas = await _rmaRepo.GetItemsAsync(queryable, requestOptions, listOptions);
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return rmas;
        }

        /// <summary>
        /// Public re-usable ListRMAsByOrderID task method
        /// </summary>
        /// <param name="orderID"></param>
        /// <param name="commerceRole"></param>
        /// <param name="me"></param>
        /// <param name="accessAllRMAsOnOrder"></param>
        /// <returns>The CosmosListPage of RMA response objects from the ListRMAsByOrderID process</returns>
        public virtual async Task<CosmosListPage<RMA>> ListRMAsByOrderID(string orderID, CommerceRole commerceRole, MeUser me, bool accessAllRMAsOnOrder = false)
        {
            var rmas = new CosmosListPage<RMA>();
            try
            {
                string sourceOrderID = orderID.Split("-")[0];
                CosmosListOptions listOptions = new CosmosListOptions() { PageSize = 100 };
                IQueryable<RMA> queryable = _rmaRepo.GetQueryable().Where(rma => rma.PartitionKey.Equals($@"PartitionValue", StringComparison.OrdinalIgnoreCase) && rma.SourceOrderID == sourceOrderID);
                if (commerceRole == CommerceRole.Supplier && !accessAllRMAsOnOrder)
                {
                    queryable = QueryOnlySupplierRMAs(queryable, me.Supplier.ID);
                }
                rmas = await GenerateRMAList(queryable, listOptions);
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return rmas;
        }

        /// <summary>
        /// Public re-usable QueryOnlySupplierRMAs method
        /// </summary>
        /// <param name="queryable"></param>
        /// <param name="supplierID"></param>
        /// <returns>The IQueryable list of RMA response object from the QueryOnlySupplierRMAs process</returns>
        public virtual IQueryable<RMA> QueryOnlySupplierRMAs(IQueryable<RMA> queryable, string supplierID)
        {
            var resp = queryable;
            try
            {
                resp = queryable.Where(rma => rma.SupplierID == supplierID);
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return resp;
        }

        /// <summary>
        /// Public re-usable ListRMAs task method
        /// </summary>
        /// <param name="listOptions"></param>
        /// <param name="decodedToken"></param>
        /// <returns>The CosmosListPage of RMA response objects from the ListRMAs process</returns>
        public async Task<CosmosListPage<RMA>> ListRMAs(CosmosListOptions listOptions, DecodedToken decodedToken)
        {
            var rmas = new CosmosListPage<RMA>();
            try
            {
                IQueryable<RMA> queryable = _rmaRepo.GetQueryable().Where(rma => rma.PartitionKey.Equals($@"PartitionValue", StringComparison.OrdinalIgnoreCase));
                if (decodedToken.CommerceRole == CommerceRole.Supplier)
                {
                    var me = await _oc.Me.GetAsync(accessToken: decodedToken.AccessToken);
                    queryable = queryable.Where(rma => rma.SupplierID == me.Supplier.ID);
                }
                rmas = await GenerateRMAList(queryable, listOptions);
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return rmas;
        }

        /// <summary>
        /// Public re-usable ProcessRMA task method
        /// </summary>
        /// <param name="rma"></param>
        /// <param name="decodedToken"></param>
        /// <returns>The RMAWithLineItemStatusByQuantity response object from the ProcessRMA process</returns>
        /// <exception cref="Exception"></exception>
        public async Task<RMAWithLineItemStatusByQuantity> ProcessRMA(RMA rma, DecodedToken decodedToken)
        {
            var rmaWithStatusByQuantityChanges = new RMAWithLineItemStatusByQuantity();
            try
            {
                // Get the RMA from the last time it was saved.
                var me = await _oc.Me.GetAsync(accessToken: decodedToken.AccessToken);
                RMA currentRMA = await GetRMA(rma.RMANumber, decodedToken);
                if (decodedToken.CommerceRole == CommerceRole.Supplier && currentRMA.SupplierID != me.Supplier.ID)
                {
                    var ex = new Exception($@"You do not have permission to process this RMA.");
                    LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
                    throw ex;
                }

                // Should the Status and IsResolved proprerties of an RMALineItem change?
                rma.LineItems = UpdateRMALineItemStatusesAndCheckIfResolved(rma.LineItems);
                // Should status of RMA change?
                rma = UpdateRMAStatus(rma);
                // If the status on the new RMA differs from the old RMA, create an RMALog
                if (rma.Status != currentRMA.Status)
                {
                    RMALog log = new RMALog() { Status = rma.Status, Date = DateTime.Now, FromUserID = me.ID };
                    rma.Logs.Insert(0, log);
                }

                HSOrderWorksheet worksheet = await _oc.IntegrationEvents.GetWorksheetAsync<HSOrderWorksheet>(OrderDirection.Incoming, rma.SourceOrderID);
                IEnumerable<RMALineItem> deniedLineItems = rma.LineItems.Where(li => !li.IsRefunded && li.Status == RMALineItemStatus.Denied)
                    .Where(li => currentRMA.LineItems.FirstOrDefault(currentLi => currentLi.ID == li.ID).Status != RMALineItemStatus.Denied).ToList();
                List<LineItemStatusChanges> lineItemStatusChangesList = BuildLineItemStatusChanges(deniedLineItems, worksheet, rma.Type, true);
                ItemResponse<RMA> updatedRMA = await _rmaRepo.ReplaceItemAsync(currentRMA.id, rma);
                await HandlePendingApprovalEmails(currentRMA, rma.LineItems, worksheet);
                rmaWithStatusByQuantityChanges = new RMAWithLineItemStatusByQuantity()
                {
                    SupplierOrderID = $"{rma.SourceOrderID}-{rma.SupplierID}",
                    RMA = updatedRMA.Resource,
                    LineItemStatusChangesList = lineItemStatusChangesList
                };
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return rmaWithStatusByQuantityChanges;
        }

        /// <summary>
        /// Private re-usable GetRMA task method
        /// </summary>
        /// <param name="rmaNumber"></param>
        /// <param name="decodedToken"></param>
        /// <returns>The RMA response object from the GetRMA process</returns>
        private async Task<RMA> GetRMA(string rmaNumber, DecodedToken decodedToken)
        {
            var currentRMA = new RMA();
            try
            {
                var currentRMAFilter = new ListFilter("RMANumber", rmaNumber);
                CosmosListOptions currentRMAListOptions = new CosmosListOptions() { PageSize = 1, ContinuationToken = null, Filters = { currentRMAFilter } };
                CosmosListPage<RMA> currentRMAListPage = await ListRMAs(currentRMAListOptions, decodedToken);
                currentRMA = currentRMAListPage.Items[0];
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return currentRMA;
        }

        /// <summary>
        /// Private re-usable UpdateRMALineItemStatusesAndCheckIfResolved task method
        /// </summary>
        /// <param name="rmaLineItems"></param>
        /// <returns>The list of RMALineItem response objects from the UpdateRMALineItemStatusesAndCheckIfResolved process</returns>
        private List<RMALineItem> UpdateRMALineItemStatusesAndCheckIfResolved(List<RMALineItem> rmaLineItems)
        {
            var resp = rmaLineItems;
            try
            {
                foreach (RMALineItem lineItem in rmaLineItems)
                {
                    // Check each LineItem on the RMA.  If Status is Approved, change to PartialQtyApproved if there is a discrepancy between QuantityRequested and Quantitiy Approved.
                    if (lineItem.Status == RMALineItemStatus.Approved || lineItem.Status == RMALineItemStatus.PartialQtyApproved)
                    {
                        if (lineItem.QuantityProcessed < lineItem.QuantityRequested)
                        {
                            lineItem.Status = RMALineItemStatus.PartialQtyApproved;
                        }
                        else
                        {
                            lineItem.Status = RMALineItemStatus.Approved;
                        }
                    }
                    // Check each LineItem on the RMA.  If the new Status is Approved, PartialQtyApproved, or Denied, IsResolved should be true.  Else, it should be false.
                    if (lineItem.Status == RMALineItemStatus.Approved || lineItem.Status == RMALineItemStatus.PartialQtyApproved || lineItem.Status == RMALineItemStatus.Denied)
                    {
                        lineItem.IsResolved = true;
                    }
                    else
                    {
                        lineItem.IsResolved = false;
                    }
                }
                resp = rmaLineItems;
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return resp;
        }

        /// <summary>
        /// Private re-usable UpdateRMAStatus method
        /// </summary>
        /// <param name="rma"></param>
        /// <returns>The RMA response objects from the UpdateRMAStatus process</returns>
        private RMA UpdateRMAStatus(RMA rma)
        {
            var resp = rma;
            try
            {
                // If any line items have a status of requested or processing, the status should be Processing.
                if (rma.LineItems.Any(li => li.Status == RMALineItemStatus.Requested || li.Status == RMALineItemStatus.Processing))
                {
                    rma.Status = RMAStatus.Processing;
                }
                // If all line items have a status of Denied, the status should be Denied.
                else if (rma.LineItems.All(li => li.Status == RMALineItemStatus.Denied))
                {
                    rma.Status = RMAStatus.Denied;
                    rma.DateComplete = DateTime.Now;
                }
                // If all line items have a status of Complete, PartialQtyComplete, and/or Denied, the status should be Complete.
                else if (rma.LineItems.All(li => li.Status == RMALineItemStatus.Complete || li.Status == RMALineItemStatus.PartialQtyComplete || li.Status == RMALineItemStatus.Denied))
                {
                    rma.Status = RMAStatus.Complete;
                    rma.DateComplete = DateTime.Now;
                }
                // If RMAs have a mixture of statuses at this point, at least one line item is approved but awaiting processing.  Set to Approved.
                else if (rma.LineItems.Any(li => li.Status == RMALineItemStatus.Approved || li.Status == RMALineItemStatus.PartialQtyApproved))
                {
                    rma.Status = RMAStatus.Approved;
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return resp;
        }

        /// <summary>
        /// Private re-usable BuildLineItemStatusChanges method
        /// </summary>
        /// <param name="rmaLineItemsToUpdate"></param>
        /// <param name="worksheet"></param>
        /// <param name="rmaType"></param>
        /// <param name="isDenyingAll"></param>
        /// <returns>The list of LineItemStatusChanges response objects from the BuildLineItemStatusChanges process</returns>
        private List<LineItemStatusChanges> BuildLineItemStatusChanges(IEnumerable<RMALineItem> rmaLineItemsToUpdate, HSOrderWorksheet worksheet, RMAType rmaType, bool isDenyingAll)
        {
            var lineItemStatusChangesList = new List<LineItemStatusChanges>();
            try
            {
                if ((!rmaLineItemsToUpdate.Any()))
                {
                    return lineItemStatusChangesList;
                }

                IEnumerable<HSLineItem> orderWorksheetLineItems = worksheet.LineItems.Where(li => rmaLineItemsToUpdate.Any(itemToUpdate => itemToUpdate.ID == li.ID));
                LineItemStatus actionCompleteType = rmaType == RMAType.Cancellation ? LineItemStatus.Canceled : LineItemStatus.Returned;
                LineItemStatus actionDeniedType = rmaType == RMAType.Cancellation ? LineItemStatus.CancelDenied : LineItemStatus.ReturnDenied;
                foreach (HSLineItem lineItem in orderWorksheetLineItems)
                {
                    RMALineItem correspondingRMALineItem = rmaLineItemsToUpdate.FirstOrDefault(li => li.ID == lineItem.ID);
                    if (correspondingRMALineItem.QuantityProcessed == correspondingRMALineItem.QuantityRequested)
                    {
                        if (isDenyingAll)
                        {
                            LineItemStatusChanges statusChangeToAdjust = lineItemStatusChangesList.FirstOrDefault(change => change.Status == actionDeniedType);
                            BuildStatusChangeToAdjust(statusChangeToAdjust, actionDeniedType, lineItemStatusChangesList, correspondingRMALineItem, isDenyingAll);
                        }
                        else
                        {
                            LineItemStatusChanges statusChangeToAdjust = lineItemStatusChangesList.FirstOrDefault(change => change.Status == actionCompleteType);
                            BuildStatusChangeToAdjust(statusChangeToAdjust, actionCompleteType, lineItemStatusChangesList, correspondingRMALineItem, isDenyingAll);
                        }
                    }
                    else
                    {
                        int quantityToComplete = correspondingRMALineItem.QuantityProcessed;
                        int quantityToDeny = correspondingRMALineItem.QuantityRequested - correspondingRMALineItem.QuantityProcessed;
                        LineItemStatusChanges statusChangeForComplete = lineItemStatusChangesList.FirstOrDefault(change => change.Status == actionCompleteType);
                        LineItemStatusChanges statusChangeForDeny = lineItemStatusChangesList.FirstOrDefault(change => change.Status == actionDeniedType);
                        BuildStatusChangeToAdjust(statusChangeForComplete, actionCompleteType, lineItemStatusChangesList, correspondingRMALineItem, isDenyingAll, quantityToComplete);
                        BuildStatusChangeToAdjust(statusChangeForDeny, actionDeniedType, lineItemStatusChangesList, correspondingRMALineItem, isDenyingAll, quantityToDeny);
                    }
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return lineItemStatusChangesList;
        }

        /// <summary>
        /// Private re-usable BuildStatusChangeToAdjust method
        /// </summary>
        /// <param name="statusChangeToAdjust"></param>
        /// <param name="lineItemStatus"></param>
        /// <param name="lineItemStatusChangesList"></param>
        /// <param name="correspondingRMALineItem"></param>
        /// <param name="isDenyingAll"></param>
        /// <param name="overriddenQuantity"></param>
        private void BuildStatusChangeToAdjust(LineItemStatusChanges statusChangeToAdjust, LineItemStatus lineItemStatus, List<LineItemStatusChanges> lineItemStatusChangesList, RMALineItem correspondingRMALineItem, bool isDenyingAll, int? overriddenQuantity = null)
        {
            try
            {
                int? quantityToChange = overriddenQuantity != null ? overriddenQuantity : correspondingRMALineItem.QuantityProcessed;
                if ((int)quantityToChange == 0)
                {
                    return;
                }

                if (statusChangeToAdjust == null)
                {
                    LineItemStatusChanges newStatusChange = new LineItemStatusChanges() { Status = lineItemStatus, Changes = new List<LineItemStatusChange>() };
                    lineItemStatusChangesList.Add(newStatusChange);
                    statusChangeToAdjust = lineItemStatusChangesList.FirstOrDefault(change => change.Status == lineItemStatus);
                }

                decimal? refundAmount = null;
                if (!isDenyingAll)
                {
                    refundAmount = correspondingRMALineItem.LineTotalRefund;
                }
                statusChangeToAdjust.Changes.Add(new LineItemStatusChange()
                {
                    ID = correspondingRMALineItem.ID,
                    Quantity = (int)quantityToChange,
                    Comment = correspondingRMALineItem.Comment,
                    Refund = refundAmount,
                    QuantityRequestedForRefund = correspondingRMALineItem.QuantityRequested,
                });
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
        }

        /// <summary>
        /// Private re-usable HandlePendingApprovalEmails task method
        /// </summary>
        /// <param name="rma"></param>
        /// <param name="rmaLineItems"></param>
        /// <param name="worksheet"></param>
        /// <returns></returns>
        private async Task HandlePendingApprovalEmails(RMA rma, List<RMALineItem> rmaLineItems, HSOrderWorksheet worksheet)
        {
            try
            {
                IEnumerable<RMALineItem> pendingApprovalLineItemsWithNewComments = rmaLineItems.Where(li => li.Status == RMALineItemStatus.Processing).Where(li => li.Comment != rma.LineItems.FirstOrDefault(item => item.ID == li.ID).Comment);
                if (!pendingApprovalLineItemsWithNewComments.Any())
                {
                    return;
                }

                LineItemStatusChanges lineItemStatusChanges = new LineItemStatusChanges()
                {
                    Status = rma.Type == RMAType.Cancellation ? LineItemStatus.CancelRequested : LineItemStatus.ReturnRequested,
                    Changes = new List<LineItemStatusChange>()
                };
                foreach (var rmaLineItem in pendingApprovalLineItemsWithNewComments)
                {
                    lineItemStatusChanges.Changes.Add(new LineItemStatusChange()
                    {
                        ID = rmaLineItem.ID,
                        Quantity = rmaLineItem.QuantityProcessed,
                        Comment = rmaLineItem.Comment,
                        QuantityRequestedForRefund = rmaLineItem.QuantityRequested
                    });

                }

                List<HSLineItem> lineItemsChanged = worksheet.LineItems.Where(li => lineItemStatusChanges.Changes.Select(li => li.ID).Contains(li.ID)).ToList();
                Supplier supplier = await _oc.Suppliers.GetAsync(rma.SupplierID);
                EmailDisplayText emailText = new EmailDisplayText()
                {
                    EmailSubject = $"New message available from {supplier.Name}",
                    DynamicText = $"{supplier.Name} has contacted you regarding your request for {rma.Type.ToString().ToLower()}",
                    DynamicText2 = "The following items have new messages"
                };
                await _sendgridService.SendLineItemStatusChangeEmail(worksheet.Order, lineItemStatusChanges, lineItemsChanged, worksheet.Order.FromUser.FirstName, worksheet.Order.FromUser.LastName, worksheet.Order.FromUser.Email, emailText);
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
        }

        /// <summary>
        /// Public re-usable ProcessRefund task method
        /// </summary>
        /// <param name="rmaNumber"></param>
        /// <param name="decodedToken"></param>
        /// <returns>The RMAWithLineItemStatusByQuantity response object from the ProcessRefund process</returns>
        public async Task<RMAWithLineItemStatusByQuantity> ProcessRefund(string rmaNumber, DecodedToken decodedToken)
        {
            var rmaWithStatusByQuantityChanges = new RMAWithLineItemStatusByQuantity();
            try
            {
                var me = await _oc.Me.GetAsync(accessToken: decodedToken.AccessToken);
                RMA rma = await GetRMA(rmaNumber, decodedToken);
                ValidateRMA(rma, me, decodedToken);

                decimal initialAmountRefunded = rma.TotalCredited;
                IEnumerable<RMALineItem> rmaLineItemsToUpdate = rma.LineItems.Where(li => !li.IsRefunded && (li.Status == RMALineItemStatus.Approved || li.Status == RMALineItemStatus.PartialQtyApproved)).ToList();
                HSOrderWorksheet worksheet = await _oc.IntegrationEvents.GetWorksheetAsync<HSOrderWorksheet>(OrderDirection.Incoming, rma.SourceOrderID);
                CosmosListPage<RMA> allRMAsOnThisOrder = await ListRMAsByOrderID(worksheet.Order.ID, decodedToken.CommerceRole, me, true);
                CalculateAndUpdateLineTotalRefund(rmaLineItemsToUpdate, worksheet, allRMAsOnThisOrder, rma.SupplierID);

                // UPDATE RMA LINE ITEM STATUSES
                SetRMALineItemStatusesToComplete(rmaLineItemsToUpdate);
                // UPDATE RMA STATUS
                UpdateRMAStatus(rma);
                await HandleRefund(rma, allRMAsOnThisOrder, worksheet, decodedToken);
                MarkRMALineItemsAsRefunded(rmaLineItemsToUpdate);
                decimal totalRefundedForThisTransaction = rma.TotalCredited - initialAmountRefunded;
                RMALog log = new RMALog() { Status = rma.Status, Date = DateTime.Now, AmountRefunded = totalRefundedForThisTransaction, FromUserID = me.ID };
                rma.Logs.Insert(0, log);

                List<LineItemStatusChanges> lineItemStatusChangesList = BuildLineItemStatusChanges(rmaLineItemsToUpdate, worksheet, rma.Type, false);
                // SAVE RMA
                ItemResponse<RMA> updatedRMA = await _rmaRepo.ReplaceItemAsync(rma.id, rma);
                rmaWithStatusByQuantityChanges = new RMAWithLineItemStatusByQuantity() { SupplierOrderID = $"{rma.SourceOrderID}-{rma.SupplierID}", RMA = updatedRMA.Resource, LineItemStatusChangesList = lineItemStatusChangesList };
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return rmaWithStatusByQuantityChanges;
        }

        /// <summary>
        /// Private re-usable ValidateRMA task method
        /// </summary>
        /// <param name="rma"></param>
        /// <param name="me"></param>
        /// <param name="decodedToken"></param>
        /// <exception cref="Exception"></exception>
        private void ValidateRMA(RMA rma, MeUser me, DecodedToken decodedToken)
        {
            try
            {
                if (decodedToken.CommerceRole == CommerceRole.Supplier && rma.SupplierID != me.Supplier.ID)
                {
                    var ex = new Exception($@"You do not have permission to process a refund for this RMA.");
                    LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
                    throw ex;
                }

                if (rma.Status == RMAStatus.Complete || rma.Status == RMAStatus.Denied || rma.Status == RMAStatus.Requested)
                {
                    var ex = new Exception($@"This RMA is not in a valid status to be refunded.");
                    LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
                    throw ex;
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
        }

        /// <summary>
        /// Public re-usable CalculateAndUpdateLineTotalRefund task method
        /// </summary>
        /// <param name="lineItemsToUpdate"></param>
        /// <param name="orderWorksheet"></param>
        /// <param name="allRMAsOnThisOrder"></param>
        /// <param name="supplierID"></param>
        public void CalculateAndUpdateLineTotalRefund(IEnumerable<RMALineItem> lineItemsToUpdate, HSOrderWorksheet orderWorksheet, CosmosListPage<RMA> allRMAsOnThisOrder, string supplierID)
        {
            try
            {
                var taxLines = orderWorksheet?.OrderCalculateResponse?.xp?.TaxCalculation?.LineItems;
                foreach (RMALineItem rmaLineItem in lineItemsToUpdate)
                {
                    if (!rmaLineItem.RefundableViaCreditCard)
                    {
                        HSLineItem lineItemFromOrder = orderWorksheet.LineItems.FirstOrDefault(li => li.ID == rmaLineItem.ID);
                        rmaLineItem.LineTotalRefund = lineItemFromOrder.LineTotal / lineItemFromOrder.Quantity * rmaLineItem.QuantityProcessed;
                    }
                    else
                    {
                        int quantityToRefund = rmaLineItem.QuantityProcessed;
                        var lineItem = orderWorksheet.LineItems.First(li => li.ID == rmaLineItem.ID);
                        var taxLine = taxLines == null ? null : taxLines.FirstOrDefault(taxLine => taxLine.LineItemID == rmaLineItem.ID);
                        var lineItemTotalTax = taxLine?.LineItemTotalTax ?? 0;

                        // Exempt products will have an exempt amount instead of a taxable amount.
                        decimal lineItemBaseCost = lineItem.xp.LineTotalWithProportionalDiscounts;
                        decimal totalRefundIfReturningAllLineItems = lineItemBaseCost + lineItemTotalTax;
                        double taxableAmountPerSingleLineItem = (double)(lineItemBaseCost / lineItem.Quantity);
                        double taxPerSingleLineItem = (double)(lineItemTotalTax / lineItem.Quantity);
                        double singleQuantityLineItemRefund = Math.Round(taxableAmountPerSingleLineItem + taxPerSingleLineItem, 2);
                        decimal expectedLineTotalRefund = (decimal)singleQuantityLineItemRefund * quantityToRefund;
                        rmaLineItem.LineTotalRefund = ValidateExpectedLineTotalRefund(expectedLineTotalRefund, totalRefundIfReturningAllLineItems, allRMAsOnThisOrder, rmaLineItem, orderWorksheet, supplierID);
                    }

                    ApplyPercentToDiscount(rmaLineItem);
                    rmaLineItem.Status = rmaLineItem.QuantityProcessed == rmaLineItem.QuantityRequested ? RMALineItemStatus.Complete : RMALineItemStatus.PartialQtyComplete;
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }            
        }

        /// <summary>
        /// Private re-usable ValidateExpectedLineTotalRefund method
        /// </summary>
        /// <param name="expectedLineTotalRefund"></param>
        /// <param name="totalRefundIfReturningAllLineItems"></param>
        /// <param name="allRMAsOnThisOrder"></param>
        /// <param name="rmaLineItem"></param>
        /// <param name="orderWorksheet"></param>
        /// <param name="supplierID"></param>
        /// <returns>The ExpectedLineTotalRefund decimal value from ValidateExpectedLineTotalRefund process</returns>
        private decimal ValidateExpectedLineTotalRefund(decimal expectedLineTotalRefund, decimal totalRefundIfReturningAllLineItems, CosmosListPage<RMA> allRMAsOnThisOrder, RMALineItem rmaLineItem, HSOrderWorksheet orderWorksheet, string supplierID)
        {
            var resp = expectedLineTotalRefund;
            try
            {
                // If minor rounding error occurs during singleQuantityLineItemRefund calculation, ensure we don't refund more than the full line item cost on the order
                // Would only occur for full quantity cancellations/returns
                if (expectedLineTotalRefund > totalRefundIfReturningAllLineItems || ShouldIssueFullLineItemRefund(rmaLineItem, allRMAsOnThisOrder, orderWorksheet, supplierID))
                {
                    return totalRefundIfReturningAllLineItems;
                }

                // If previous RMAs on this order for the same line item
                if (allRMAsOnThisOrder.Items.Count > 1)
                {
                    decimal previouslyRefundedAmountForThisLineItem = 0M;
                    // Find previously refunded total for line items on this order...
                    foreach (RMA previouslyRefundedRMA in allRMAsOnThisOrder.Items)
                    {
                        RMALineItem previouslyRefundedLineItem = previouslyRefundedRMA.LineItems.FirstOrDefault(li => li.ID == rmaLineItem.ID);
                        if (previouslyRefundedLineItem != null)
                        {
                            if (previouslyRefundedLineItem.IsRefunded && previouslyRefundedLineItem.RefundableViaCreditCard)
                            {
                                previouslyRefundedAmountForThisLineItem += previouslyRefundedLineItem.LineTotalRefund;
                            }
                        }
                    }
                    // If previous total + new line total > totalRefundIfReturningAllLineItems, then totalRefundIfReturningAllLineItems - previousTotal = newLineTotal
                    if (previouslyRefundedAmountForThisLineItem + expectedLineTotalRefund > totalRefundIfReturningAllLineItems)
                    {
                        decimal totalAfterPossibleRoundingErrors = totalRefundIfReturningAllLineItems - previouslyRefundedAmountForThisLineItem;
                        return totalAfterPossibleRoundingErrors;
                    }
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return resp;
        }


        /// <summary>
        /// Private re-usable ShouldIssueFullLineItemRefund method
        /// </summary>
        /// <param name="rmaLineItem"></param>
        /// <param name="allRMAsOnThisOrder"></param>
        /// <param name="orderWorksheet"></param>
        /// <param name="supplierID"></param>
        /// <returns>The Should IssueFullLineItemRefund boolean status value from the ShouldIssueFullLineItemRefund process</returns>
        private bool ShouldIssueFullLineItemRefund(RMALineItem rmaLineItem, CosmosListPage<RMA> allRMAsOnThisOrder, HSOrderWorksheet orderWorksheet, string supplierID)
        {
            var resp = false;
            try
            {
                var lineItem = orderWorksheet.LineItems.First(li => li.ID == rmaLineItem.ID);
                var rmasFromThisSupplier = allRMAsOnThisOrder.Items.Where(r => r.SupplierID == supplierID);
                // If this is the only RMA for this line item and all requested RMA quantity are approved, and the quantity equals the original order quantity, issue a full refund (line item cost + tax).
                if (rmaLineItem.Status == RMALineItemStatus.Approved && rmaLineItem.QuantityProcessed == lineItem.Quantity && rmasFromThisSupplier.Count() == 1)
                {
                    resp = true;
                }
                else
                {
                    resp = false;
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return resp;
        }

        /// <summary>
        /// Public re-usable ApplyPercentToDiscount method
        /// </summary>
        /// <param name="rmaLineItem"></param>
        /// <exception cref="Exception"></exception>
        public virtual void ApplyPercentToDiscount(RMALineItem rmaLineItem)
        {
            try
            {
                if (rmaLineItem.PercentToRefund <= 0 || rmaLineItem.PercentToRefund > 100)
                {
                    var ex = new Exception($@"The refund percentage for the {rmaLineItem.ID} must be greater than 0 and no higher than 100 percent.");
                    LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
                    throw ex;
                }
                if (rmaLineItem.PercentToRefund != null)
                {
                    // Math.Round() by default would round 13.745 to 13.74 based on banker's rounding (going to the nearest even number when the final digit is 5).
                    // This errs on the side of rounding up (away from zero) when only a percentage of a refund is applied.
                    rmaLineItem.LineTotalRefund = Math.Round((decimal)(rmaLineItem.LineTotalRefund / 100 * rmaLineItem.PercentToRefund), 2, MidpointRounding.AwayFromZero);
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
        }

        /// <summary>
        /// Private re-usable SetRMALineItemStatusesToComplete method
        /// </summary>
        /// <param name="rmaLineItemsToUpdate"></param>
        private void SetRMALineItemStatusesToComplete(IEnumerable<RMALineItem> rmaLineItemsToUpdate)
        {
            try
            {
                foreach (RMALineItem lineItem in rmaLineItemsToUpdate)
                {
                    if (lineItem.QuantityRequested == lineItem.QuantityProcessed)
                    {
                        lineItem.Status = RMALineItemStatus.Complete;
                    }
                    else
                    {
                        lineItem.Status = RMALineItemStatus.PartialQtyComplete;
                    }
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
        }

        /// <summary>
        /// Public re-usable HandleRefund task method
        /// </summary>
        /// <param name="rma"></param>
        /// <param name="allRMAsOnThisOrder"></param>
        /// <param name="worksheet"></param>
        /// <param name="decodedToken"></param>
        /// <returns></returns>
        public virtual async Task HandleRefund(RMA rma, CosmosListPage<RMA> allRMAsOnThisOrder, HSOrderWorksheet worksheet, DecodedToken decodedToken)
        {
            try
            {
                // Get payment info from the order
                ListPage<HSPayment> paymentResponse = await _oc.Payments.ListAsync<HSPayment>(OrderDirection.Incoming, rma.SourceOrderID);
                HSPayment creditCardPayment = paymentResponse.Items.FirstOrDefault(payment => payment.Type == OrderCloud.SDK.PaymentType.CreditCard);
                if (creditCardPayment == null)
                {
                    // Items were not paid for with a credit card.  No refund to process via CardConnect.
                    return;
                }
                HSPaymentTransaction creditCardPaymentTransaction = creditCardPayment.Transactions.OrderBy(x => x.DateExecuted).LastOrDefault(x => x.Type.Equals($@"CreditCard", StringComparison.OrdinalIgnoreCase) && x.Succeeded);
                decimal purchaseOrderTotal = (decimal)paymentResponse.Items.Where(payment => payment.Type == PaymentType.PurchaseOrder).Select(payment => payment.Amount).Sum();

                // Refund via CardConnect
                CardConnectInquireResponse inquiry = await _cardConnect.Inquire(new CardConnectInquireRequest
                {
                    merchid = creditCardPaymentTransaction.xp.CardConnectResponse.merchid,
                    orderid = rma.SourceOrderID,
                    set = $@"1",
                    currency = worksheet.Order.xp.Currency.ToString(),
                    retref = creditCardPaymentTransaction.xp.CardConnectResponse.retref
                });

                decimal shippingRefund = rma.Type == RMAType.Cancellation ? GetShippingRefundIfCancellingAll(worksheet, rma, allRMAsOnThisOrder) : 0M;
                decimal lineTotalToRefund = rma.LineItems.Where(li => li.IsResolved && !li.IsRefunded && li.RefundableViaCreditCard && (li.Status == RMALineItemStatus.PartialQtyComplete || li.Status == RMALineItemStatus.Complete))
                    .Select(li => li.LineTotalRefund).Sum();
                decimal totalToRefund = (lineTotalToRefund + shippingRefund);
                // Update Total Credited on RMA
                rma.TotalCredited += totalToRefund;
                // Transactions that are queued for capture can only be fully voided, and we are only allowing partial voids moving forward.
                if (inquiry.voidable.Equals($@"Y", StringComparison.OrdinalIgnoreCase) && inquiry.setlstat == QUEUED_FOR_CAPTURE)
                {
                    var ex = new CatalystBaseException(new ApiError
                    {
                        ErrorCode = $@"Payment.FailedToVoidAuthorization",
                        Message = $@"This customer's credit card transaction is currently queued for capture and cannot be refunded at this time.  Please try again later."
                    });
                    LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
                    throw ex;
                }

                // If voidable, but not refundable, void the refund amount off the original order total
                if (inquiry.voidable.Equals($@"Y", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleVoidTransaction(worksheet, creditCardPayment, creditCardPaymentTransaction, totalToRefund, rma);
                }
                // If refundable, but not voidable, do a refund
                if (inquiry.voidable.Equals($@"N", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleRefundTransaction(worksheet, creditCardPayment, creditCardPaymentTransaction, totalToRefund, rma);
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
        }

        /// <summary>
        /// Public re-usable GetShippingRefundIfCancellingAll task method
        /// </summary>
        /// <param name="worksheet"></param>
        /// <param name="rma"></param>
        /// <param name="allRMAsOnThisOrder"></param>
        /// <returns>The decimal value from the GetShippingRefundIfCancellingAll process</returns>
        public virtual decimal GetShippingRefundIfCancellingAll(HSOrderWorksheet worksheet, RMA rma, CosmosListPage<RMA> allRMAsOnThisOrder)
        {
            var resp = 0M;
            try
            {
                // What are all the line items on this order for this supplier and their quantities?
                IEnumerable<HSLineItem> allLineItemsShippedFromThisSupplier = worksheet.LineItems.Where(li => li.SupplierID == rma.SupplierID && worksheet.Order.xp.PaymentMethod.Equals("Credit Card", StringComparison.OrdinalIgnoreCase));
                Dictionary<string, int> allLineItemsDictionary = new Dictionary<string, int>();
                foreach (HSLineItem li in allLineItemsShippedFromThisSupplier)
                {
                    allLineItemsDictionary.Add(li.ID, li.Quantity);
                }

                // Including this RMA and previous RMAs for this supplier, get everything that has been refunded or is about to be refunded.
                var rmasFromThisSupplier = allRMAsOnThisOrder.Items.Where(r => r.SupplierID == rma.SupplierID);
                Dictionary<string, int> allCompleteRMALineItemsDictionary = new Dictionary<string, int>();
                foreach (RMA existingRMA in rmasFromThisSupplier)
                {
                    RMA rmaToAnalyze = existingRMA.RMANumber == rma.RMANumber ? rma : existingRMA;
                    foreach (RMALineItem rmaLineItem in rmaToAnalyze.LineItems)
                    {
                        if (rmaLineItem.Status == RMALineItemStatus.Complete && rmaLineItem.RefundableViaCreditCard)
                        {
                            if (!allCompleteRMALineItemsDictionary.ContainsKey(rmaLineItem.ID))
                            {
                                allCompleteRMALineItemsDictionary.Add(rmaLineItem.ID, rmaLineItem.QuantityProcessed);
                            }
                            else
                            {
                                allCompleteRMALineItemsDictionary[rmaLineItem.ID] += rmaLineItem.QuantityProcessed;
                            }
                        }
                    }
                }

                // If these are the same, the supplier hasn't shipped anything, and shipping should be credited back to the buyer.
                bool shouldShippingBeCanceled = allLineItemsDictionary.OrderBy(kvp => kvp.Key).SequenceEqual(allCompleteRMALineItemsDictionary.OrderBy(kvp => kvp.Key));
                // Figure out what the buyer paid for shipping for this supplier on this order.
                if (shouldShippingBeCanceled)
                {
                    string shipEstimateID = worksheet.ShipEstimateResponse.ShipEstimates.FirstOrDefault(estimate => estimate.xp.SupplierID == rma.SupplierID)?.ID;
                    var shippingLine = worksheet.OrderCalculateResponse.xp.TaxCalculation.OrderLevelTaxes.First(tax => tax.ShipEstimateID == shipEstimateID);
                    decimal shippingCostToRefund = (decimal)(shippingLine.Taxable + shippingLine.Tax + shippingLine.Exempt);
                    rma.ShippingCredited += shippingCostToRefund;
                    return shippingCostToRefund;
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
            return resp;
        }

        /// <summary>
        /// Public re-usable HandleVoidTransaction task method
        /// </summary>
        /// <param name="worksheet"></param>
        /// <param name="creditCardPayment"></param>
        /// <param name="creditCardPaymentTransaction"></param>
        /// <param name="totalToRefund"></param>
        /// <param name="rma"></param>
        /// <returns></returns>
        /// <exception cref="CatalystBaseException"></exception>
        public virtual async Task HandleVoidTransaction(HSOrderWorksheet worksheet, HSPayment creditCardPayment, HSPaymentTransaction creditCardPaymentTransaction, decimal totalToRefund, RMA rma)
        {
            try
            {
                HSPayment newCreditCardVoid = new HSPayment() { Amount = totalToRefund };
                try
                {
                    CardConnectVoidResponse response = await _cardConnect.VoidAuthorization(new CardConnectVoidRequest
                    {
                        currency = worksheet.Order.xp.Currency.ToString(),
                        merchid = creditCardPaymentTransaction.xp.CardConnectResponse.merchid,
                        retref = creditCardPaymentTransaction.xp.CardConnectResponse.retref,
                        amount = totalToRefund.ToString("F2"),
                    });
                    await _oc.Payments.CreateTransactionAsync(OrderDirection.Incoming, rma.SourceOrderID, creditCardPayment.ID, CardConnectMapper.Map(newCreditCardVoid, response));
                }
                catch (CreditCardVoidException ex)
                {
                    await _oc.Payments.CreateTransactionAsync(OrderDirection.Incoming, rma.SourceOrderID, creditCardPayment.ID, CardConnectMapper.Map(newCreditCardVoid, ex.Response));
                    var ex1 = new CatalystBaseException(new ApiError
                    {
                        ErrorCode = $@"Payment.FailedToVoidAuthorization",
                        Message = ex.ApiError.Message
                    });
                    LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", $@"{ex.Message}. {ex1.Message}.", ex.StackTrace, this, true);
                    throw ex1;
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
        }

        /// <summary>
        /// Public re-usable HandleRefundTransaction task method
        /// </summary>
        /// <param name="worksheet"></param>
        /// <param name="creditCardPayment"></param>
        /// <param name="creditCardPaymentTransaction"></param>
        /// <param name="totalToRefund"></param>
        /// <param name="rma"></param>
        /// <returns></returns>
        /// <exception cref="CatalystBaseException"></exception>
        public virtual async Task HandleRefundTransaction(HSOrderWorksheet worksheet, HSPayment creditCardPayment, HSPaymentTransaction creditCardPaymentTransaction, decimal totalToRefund, RMA rma)
        {
            try
            {
                CardConnectRefundRequest requestedRefund = new CardConnectRefundRequest()
                {
                    currency = worksheet.Order.xp.Currency.ToString(),
                    merchid = creditCardPaymentTransaction.xp.CardConnectResponse.merchid,
                    retref = creditCardPaymentTransaction.xp.CardConnectResponse.retref,
                    amount = totalToRefund.ToString($@"F2"),
                };
                CardConnectRefundResponse response = await _cardConnect.Refund(requestedRefund);
                HSPayment newCreditCardPayment = new HSPayment() { Amount = response.amount };
                await _oc.Payments.CreateTransactionAsync(OrderDirection.Incoming, rma.SourceOrderID, creditCardPayment.ID, CardConnectMapper.Map(newCreditCardPayment, response));
            }
            catch (CreditCardRefundException ex)
            {
                var ex1 = new CatalystBaseException(new ApiError
                {
                    ErrorCode = $@"Payment.FailedToRefund",
                    Message = ex.ApiError.Message
                });
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", $@"{ex.Message}. {ex1.Message}.", ex.StackTrace, this, true);
                throw ex1;
            }
        }

        /// <summary>
        /// Public re-usable MarkRMALineItemsAsRefunded task method
        /// </summary>
        /// <param name="rmaLineItemsToUpdate"></param>
        private void MarkRMALineItemsAsRefunded(IEnumerable<RMALineItem> rmaLineItemsToUpdate)
        {
            try
            {
                foreach (RMALineItem rmaLineItem in rmaLineItemsToUpdate)
                {
                    rmaLineItem.IsRefunded = true;
                }
            }
            catch (Exception ex)
            {
                LogExt.LogException(_webConfigSettings.AppLogFileKey, Helpers.GetMethodName(), $@"{LoggingNotifications.GetGeneralLogMessagePrefixKey()}", ex.Message, ex.StackTrace, this, true);
            }
        }
    }
}