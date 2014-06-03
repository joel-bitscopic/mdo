﻿#region CopyrightHeader
//
//  Copyright by Contributors
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0.txt
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
#endregion

using System;
using System.Collections.Generic;
using System.Text;
using gov.va.medora.mdo.exceptions;

namespace gov.va.medora.mdo.dao.vista
{
    public class VistaClaimsDao : IClaimsDao
    {
        AbstractConnection cxn = null;

        public VistaClaimsDao(AbstractConnection cxn)
        {
            this.cxn = cxn;
        }

        public List<ProstheticClaim> getProstheticClaims(string dfn, List<string> episodeDates)
        {
            ProstheticClaim[] allClaims = getProstheticClaimsForClaimant(dfn);
            List<ProstheticClaim> myClaims = new List<ProstheticClaim>();
            for (int i = 0; i < allClaims.Length; i++)
            {
                string key = convertDate(allClaims[i].EpisodeDate);
                if (episodeDates.Contains(key))
                {
                    myClaims.Add(allClaims[i]);
                    addMoreProstheticClaimData(allClaims[i]);
                }
            }
            return myClaims;
        }

        internal string convertDate(string mdoDate)
        {
            if (mdoDate.Contains("."))
            {
                mdoDate = mdoDate.Substring(0, 8);
            }
            string yr = mdoDate.Substring(0, 4).Substring(2);
            string mo = mdoDate.Substring(4, 2);
            if (mo[0] == '0')
            {
                mo = mo.Substring(1);
            }
            string dy = mdoDate.Substring(6);
            return mo + dy + yr;
        }

        internal void addMoreProstheticClaimData(ProstheticClaim claim)
        {
            if (!hasMoreProstheticClaimData(claim.ItemId))
            {
                return;
            }
            string arg = "$P($G(^RMPR(660," + claim.ItemId + ",1)),U,4)" + "_U_" +
                         "$P($G(^RMPR(660," + claim.ItemId + ",0)),U,16)" + "_U_" +
                         "$P($G(^RMPR(660," + claim.ItemId + ",10)),U,9)";
            string response = VistaUtils.getVariableValue(cxn, arg);
            string[] flds = response.Split(new char[] { '^' });
            claim.Cost = flds[1];
            claim.ConsultId = flds[2];

            arg = "$P($G(^RMPR(661.1," + flds[0] + ",0)),U,2)";
            response = VistaUtils.getVariableValue(cxn, arg);
            claim.ItemName = response;
        }

        internal bool hasMoreProstheticClaimData(string claimId)
        {
            string arg = "$D(^RMPR(660," + claimId + ",0))";
            string response = VistaUtils.getVariableValue(cxn, arg);
            return response == "1";
        }

        public ProstheticClaim[] getProstheticClaimsForClaimant()
        {
            return getProstheticClaimsForClaimant(cxn.Pid);
        }

        public ProstheticClaim[] getProstheticClaimsForClaimant(string dfn)
        {
            DdrLister query = buildGetProstheticClaimsForPatientQuery(dfn);
            string[] response = query.execute();
            return toProstheticClaims(response);
        }

        internal DdrLister buildGetProstheticClaimsForPatientQuery(string dfn)
        {
            DdrLister query = new DdrLister(cxn);
            query.File = "356";
            query.Fields = ".02;.02E;.06;.09;1.01;1.03";
            query.Flags = "IP";
            query.Xref = "C";
            query.From = VistaUtils.adjustForNumericSearch(dfn);
            query.Part = dfn;
            query.Screen = "I $P(^(0),U,9)'=\"\"";
            return query;
        }

        internal ProstheticClaim[] toProstheticClaims(string[] response)
        {
            if (response == null || response.Length == 0)
            {
                return null;
            }
            List<ProstheticClaim> lst = new List<ProstheticClaim>(response.Length);
            for (int i = 0; i < response.Length; i++)
            {
                if (response[i] == "")
                {
                    continue;
                }
                response[i] = response[i].Replace("&#94;", "^");
                string[] flds = response[i].Split(new char[] { '^' });
                if (flds.Length == 0)
                {
                    continue;
                }
                ProstheticClaim claim = new ProstheticClaim();
                claim.Id = flds[0];
                if (flds.Length > 1)
                {
                    claim.PatientId = flds[1];
                }
                if (flds.Length > 2)
                {
                    claim.PatientName = flds[2];
                }
                if (flds.Length > 3)
                {
                    claim.EpisodeDate = VistaTimestamp.toUtcString(flds[3]);
                }
                if (flds.Length > 4)
                {
                    claim.ItemId = flds[4];
                }
                if (flds.Length > 5)
                {
                    claim.Timestamp = VistaTimestamp.toUtcString(flds[5]);
                }
                if (flds.Length > 6)
                {
                    claim.LastEditTimestamp = VistaTimestamp.toUtcString(flds[6]);
                }
                lst.Add(claim);
            }
            return (ProstheticClaim[])lst.ToArray();
        }

        public List<Person> getClaimants(string lastName, string firstName, string middleName, string dob, Address addr, int maxrex)
        {
            throw new NotImplementedException();
        }
    }
}
