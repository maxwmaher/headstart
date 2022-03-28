# Sitecore CDP Integration For OrderCloud Headstart

## Scope of this integration
This integration tracks user interactions on the Storefront and forwards these events to Sitecore CDP. 

Events 
- User Identify 
- Url Path Changes (all page views tracked)
- Product Search
- Add To Cart
- Cart Cleared
- Place Order
- Ability to create any custom event

## Sitecore CDP Basics 
[Sitecore CDP](https://www.sitecore.com/products/customer-data-platform) transforms marketing efforts into intelligent experiences that acquire, serve, and retain customers. Capture, unify, and activate omnichannel customer data.

- Segment data based on users that share common attributes, behaviors or transactions
- Implement a "privacy by design" approach to your organization's data with PII and non-PII configuration options
- Harness first-, second-, and third-party data to uncover brilliant and actionable customer insights
- Share and activate learnings from real-time audience insights across your organization 
- Collect, connect, and unify customer data from any channel

See [this guide](https://doc.sitecore.com/cdp/en/developers/sitecore-customer-data-platform--data-model-2-0/javascript-tagging-examples-for-web-pages.html) for tracking users with javascript. 

## Steps to use
- Set up the headstart application. This is process is throughly documented [here](https://github.com/ordercloud-api/headstart#initial-setup).
- Sign up for Sitecore CDP and copy an ApiClient value.  
- Set up FE buyer environment variables (in Buyer/src/assets/appConfigs) required for CDP. Please see [Sitecore CDP documentation](https://doc.sitecore.com/cdp/en/developers/sitecore-customer-data-platform--data-model-2-1/javascript-tagging-examples-for-webpages.html) for additional details.
- Use the correct endpoint for your region 
Europe: https://api.boxever.com/v1.2

Asia Pacific: https://api-ap-southeast-2-production.boxever.com/v1.2

United States: https://api-us.boxever.com/v1.2
```json
  "useSitecoreCDP": true,
  "sitecoreCDPTargetEndpoint": "https://api-us.boxever.com/v1.2",
  "sitecoreCDPApiClient": "xxxxxxxxxxxxxx",
  "sitecoreCDPCookieDomain": "your website url without www",
  "sitecoreCDPJavascriptLibraryVersion": "1.4.8",
  "sitecoreCDPWebFlowTarget": "https://d35vb5cccm4xzp.cloudfront.net", //the value will remain the same unless advised otherwise by the product team 
  "sitecoreCDPPointOfSale": "pos name setup in CDP"
```

