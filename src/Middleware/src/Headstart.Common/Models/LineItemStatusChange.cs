using System.Collections.Generic;

namespace Headstart.Common.Models
{
    // a line item status change always changes lineitems to one status as the change triggers an email related to that change
    // changing to multiple different statuses can be done in multiple calls which will trigger multiple emails

    /* the previous status that are being changed need to be defined, for example if a line item has return requested for 2 quantity
    * and completed for 2 others and the user is setting 3 quantity to returned we need to know which quantities to decrement when
    * incrementing the returned count */

    public class LineItemStatusChanges
    {
        public LineItemStatus Status { get; set; }

        public List<LineItemStatusChange> Changes { get; set; }

        public SuperHSShipment SuperShipment { get; set; }
    }

    public class LineItemStatusChange
    {
        public string ID { get; set; }

        public int Quantity { get; set; }
    }
}
