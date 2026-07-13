# ADR-061: Gateway weighted canary (Restaurant)

**Status:** Accepted  
**Decision:** YARP cluster `restaurant` with destinations `stable` (:5260) and `canary` (:5261); policy `WeightedCanary` uses `Canary:RestaurantPercent`. Canary process sets `Restaurant:Buggy=true` for demo errors.  
**Rollback:** set percent to 0 (no redeploy of stable).  
