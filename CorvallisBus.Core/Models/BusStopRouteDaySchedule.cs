﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CorvallisBus.Core.Models
{
    /// <summary>
    /// Represents the schedule for a particular route at a particular stop on a particular set of days.
    /// </summary>
    public class BusStopRouteDaySchedule
    {
        public DaysOfWeek Days { get; }
        public List<TimeSpan> Times { get; }

        public BusStopRouteDaySchedule(
            DaysOfWeek days,
            List<TimeSpan> times)
        {
            Days = days;
            Times = times;
        }
    }
}
