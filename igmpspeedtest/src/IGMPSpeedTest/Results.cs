using System.Collections.Generic;
using System.Net;

namespace IGMPSpeedTest
{
    /// <summary>
    /// IgmpSpeedTest - measures IGMP JOIN and LEAVE performance
    /// 
    /// Copyright(C) 2016 NBCUniversal
    /// 
    /// This library is free software; you can redistribute it and/or
    /// modify it under the terms of the GNU Lesser General Public
    /// License as published by the Free Software Foundation; either
    /// version 2.1 of the License, or(at your option) any later version.
    /// 
    /// This library is distributed in the hope that it will be useful,
    /// but WITHOUT ANY WARRANTY; without even the implied warranty of
    /// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the GNU
    /// Lesser General Public License for more details.
    /// 
    /// You should have received a copy of the GNU Lesser General Public
    /// License along with this library; if not, write to the Free Software
    /// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
    /// 
    /// Developed by intoto systems as a work-for-hire for NBCUniversal, and released
    /// under the LGPL per NBCUniversal request.
    class Results
    {
        public Dictionary<IPAddress, long> JoinTime = new Dictionary<IPAddress, long>();
        public Dictionary<IPAddress, long> LeaveTime = new Dictionary<IPAddress, long>();
        public bool HasResults = false;
        public bool IsComplete = false;
        public float FastestJoin=-1;
        public float AverageJoin=-1;
        public float SlowestJoin=-1;
        public float FastestLeave = -1;
        public float AverageLeave = -1;
        public float SlowestLeave = -1;
    }
}
