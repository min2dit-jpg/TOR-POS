import { router, json } from '@appdeploy/sdk';

import { notifySubscribers, realtimeSubscriptionRoutes } from "./realtime-subscribers";

export const handler = router({
    'GET /api/_healthcheck':[async()=>json({message:'Success'})],'POST /api/site':[async()=>json({products:[{name:'Burger',price:'7,90 €'},{name:'Pommes',price:'3,50 €'},{name:'Nuggets 6er',price:'5,90 €'},{name:'Getränk',price:'2,80 €'},{name:'Menü 1',price:'12,90 €'},{name:'Menü 2',price:'14,50 €'}]})],

    ...realtimeSubscriptionRoutes,
})