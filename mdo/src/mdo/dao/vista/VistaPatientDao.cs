#region CopyrightHeader
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
using System.Collections.Specialized;
using System.Collections;
using System.Text;
using gov.va.medora.utils;
using gov.va.medora.mdo.exceptions;
using gov.va.medora.mdo.src.mdo;

namespace gov.va.medora.mdo.dao.vista
{
    public class VistaPatientDao : IPatientDao
    {
        AbstractConnection cxn = null;
        private bool isNameSearch;

        public VistaPatientDao(AbstractConnection cxn)
        {
            this.cxn = cxn;
        }

        #region Patient Lookups

        public Patient[] match(string target)
        {
            MdoQuery request = buildMatchRequest(target);
            string response = (string)cxn.query(request);
            return toMatches(response);
        }

        internal MdoQuery buildMatchRequest(string target)
        {
            isNameSearch = false;
            if (target == null || target.Length < 2)
            {
                throw new MdoException(MdoExceptionCode.ARGUMENT_INVALID, "Invalid search criteria. Target length must be greater than 1: "  + target);
            }
            VistaQuery vq = new VistaQuery();

            // If first char is numeric, must be SSN
            if (StringUtils.isNumericChar(target[0]))
            {
                if (!SocSecNum.isWellFormed(target))
                {
                    throw new MdoException(MdoExceptionCode.ARGUMENT_INVALID, "Invalid SSN: " + target);
                }
                
                string ssn = StringUtils.removeNonNumericChars(target);
              
                vq.RpcName = "ORWPT FULLSSN";
                vq.addParameter(vq.LITERAL, ssn);
                return vq;
            }

            // First char was not numeric.  Make sure it's alpha.
            target = target.ToUpper();
            if (!StringUtils.isAlphaChar(target[0]))
            {
                throw new MdoException(MdoExceptionCode.ARGUMENT_INVALID, "Invalid search criteria. Target must start with with an alpha character: " + target);
            }

            // First char was alpha.  If second char is numeric, must be last5.
            if (StringUtils.isNumericChar(target[1]))
            {
                if (target.Length != 5)
                {
                    throw new MdoException(MdoExceptionCode.ARGUMENT_INVALID, "Invalid last 5 identifier: " + target);
                }
                for (int i = 2; i < 5; i++)
                {
                    if (!StringUtils.isNumericChar(target[i]))
                    {
                        throw new MdoException(MdoExceptionCode.ARGUMENT_INVALID, "Invalid last 5 identifier: " + target);
                    }
                }
                vq.RpcName = "ORWPT LAST5";
                vq.addParameter(vq.LITERAL, target);
                return vq;
            }

            // Second char was not numeric.  Assume it's a preferredTerm.
            if (!PersonName.isValid(target))
            {
                throw new MdoException(MdoExceptionCode.ARGUMENT_INVALID, "Invalid person name: " + target);
            }
            isNameSearch = true;
            vq.RpcName = "ORWPT LIST ALL";
            vq.addParameter(vq.LITERAL, VistaUtils.adjustForNameSearch(target));
            vq.addParameter(vq.LITERAL, "1");
            return vq;
        }

        internal Patient[] toMatches(string response)
        {
            string[] lines = StringUtils.split(response, StringUtils.CRLF);
            ArrayList lst = new ArrayList(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i] == "")
                {
                    continue;
                }
                Patient p = toMatch(lines[i]);
                if (p != null)
                {
                    lst.Add(p);
                }
            }
            return (Patient[])lst.ToArray(typeof(Patient));
        }

        internal Patient toMatch(string line)
        {
            Patient p = new Patient();
            string[] fields = StringUtils.split(line, StringUtils.CARET);
            p.LocalPid = fields[0];
            int nameFldIdx = isNameSearch ? 5 : 1;
            p.setName(fields[nameFldIdx]);
            if (fields.Length == 2)
            {
                return p;
            }
            if (fields[2] != "")
            {
                p.DOB = VistaTimestamp.toUtcString(fields[2]);
            }
            if (fields[3] != "")
            {
                if (SocSecNum.isValid(fields[3]))
                {
                    p.SSN = new SocSecNum(fields[3]);
                }
                else
                {
                    p.SSN = new SocSecNum(true);
                }
            }
            return p;
        }

        public Patient[] getPatientsByWard(string wardIen)
        {
            VistaUtils.CheckRpcParams(wardIen);
            VistaQuery vq = new VistaQuery("ORWPT BYWARD");
            vq.addParameter(vq.LITERAL, wardIen);
            string response = (string)cxn.query(vq);
            return toPatientsFromWard(response);
        }

        internal Patient[] toPatientsFromWard(string response)
        {
            if (response == "" || response.IndexOf("No patients found") != -1)
            {
                return null;
            }
            string[] lines = StringUtils.split(response, StringUtils.CRLF);
            lines = StringUtils.trimArray(lines);
            Patient[] result = new Patient[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                string[] flds = StringUtils.split(lines[i], StringUtils.CARET);
                result[i] = new Patient();
                result[i].LocalPid = flds[0];
                result[i].Name = new PersonName(flds[1]);
                if (flds.Length > 2 && flds[2] != "")
                {
                    string[] parts = StringUtils.split(flds[2], "-");
                    result[i].Location = new HospitalLocation();
                    result[i].Location.Room = parts[0];
                    result[i].Location.Bed = parts[1];
                }
                result[i].IsInpatient = true;
            }
            return result;
        }

        public Patient[] getPatientsByClinic(string clinicIen)
        {
            return getPatientsByClinic(clinicIen, "T", "T");
        }

        public Patient[] getPatientsByClinic(string clinicIen, string fromDate, string toDate)
        {
            MdoQuery request = buildGetPatientsByClinicRequest(clinicIen, fromDate, toDate);
            string response = (string)cxn.query(request);
            return toPatientsFromClinic(response);
        }

        internal MdoQuery buildGetPatientsByClinicRequest(string clinicIen, string fromDate, string toDate)
        {
            if (fromDate.Equals("T") && toDate.Equals("T"))
                VistaUtils.CheckRpcParams(clinicIen);
            else
                VistaUtils.CheckRpcParams(clinicIen, fromDate, toDate);

            VistaQuery vq = new VistaQuery("ORQPT CLINIC PATIENTS");
            vq.addParameter(vq.LITERAL, clinicIen);
            if (fromDate == "T")
            {
                vq.addParameter(vq.LITERAL, "T");
            }
            else
            {
                vq.addParameter(vq.LITERAL, VistaTimestamp.fromUtcString(fromDate));
            }
            if (toDate == "T")
            {
                vq.addParameter(vq.LITERAL, "T");
            }
            else
            {
                vq.addParameter(vq.LITERAL, VistaTimestamp.fromUtcString(toDate));
            }
            return vq;
        }

        internal Patient[] toPatientsFromClinic(string response)
        {
            if (response == "" || response.IndexOf("No appointments") != -1)
            {
                return null;
            }

            if (StringUtils.piece(response, "^", 1) == "")
            {
                if (response.IndexOf("ERROR") != -1)
                    throw new MdoException(MdoExceptionCode.VISTA_DATA_ERROR, StringUtils.piece(response, "^", 2).Trim());

                return null;
            }
            string[] lines = StringUtils.split(response, StringUtils.CRLF);
            lines = StringUtils.trimArray(lines);
            Patient[] result = new Patient[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                string[] flds = StringUtils.split(lines[i], StringUtils.CARET);
                result[i] = new Patient();
                result[i].LocalPid = flds[0];
                result[i].Name = new PersonName(flds[1]);
                result[i].Location = new HospitalLocation(flds[2], flds[8]);
                result[i].Location.AppointmentTimestamp = VistaTimestamp.toUtcString(flds[3]);
                result[i].IsInpatient = false;
            }
            return result;
        }

        public Patient[] getPatientsByTeam(string teamIen)
        {
            VistaUtils.CheckRpcParams(teamIen);
            VistaQuery vq = new VistaQuery("ORQPT TEAM PATIENTS");
            vq.addParameter(vq.LITERAL, teamIen);
            string response = (string)cxn.query(vq);
            if (response.IndexOf("No patients found") != -1)
            {
                return null;
            }
            string[] lines = StringUtils.split(response, StringUtils.CRLF);
            lines = StringUtils.trimArray(lines);
            Patient[] result = new Patient[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                string[] flds = StringUtils.split(lines[i], StringUtils.CARET);
                result[i] = new Patient();
                result[i].LocalPid = flds[0];
                result[i].Name = new PersonName(flds[1]);
            }
            return result;
        }

        public Patient[] getPatientsBySpecialty(string specialtyIen)
        {
            VistaUtils.CheckRpcParams(specialtyIen);
            VistaQuery vq = new VistaQuery("ORQPT SPECIALTY PATIENTS");
            vq.addParameter(vq.LITERAL, specialtyIen);
            string response = (string)cxn.query(vq);
            if (response.IndexOf("No patients found") != -1)
            {
                return null;
            }
            string[] lines = StringUtils.split(response, StringUtils.CRLF);
            lines = StringUtils.trimArray(lines);
            Patient[] result = new Patient[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                string[] flds = StringUtils.split(lines[i], StringUtils.CARET);
                result[i] = new Patient();
                result[i].LocalPid = flds[0];
                result[i].Name = new PersonName(flds[1]);
            }
            return result;
        }

        public Patient[] getPatientsByProvider(string duz)
        {
            VistaUtils.CheckRpcParams(duz);
            VistaQuery vq = new VistaQuery("ORQPT PROVIDER PATIENTS");
            vq.addParameter(vq.LITERAL, duz);
            string response = (string)cxn.query(vq);
            if (response.IndexOf("No patients found") != -1)
            {
                return null;
            }
            string[] lines = StringUtils.split(response, StringUtils.CRLF);
            lines = StringUtils.trimArray(lines);
            Patient[] result = new Patient[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                string[] flds = StringUtils.split(lines[i], StringUtils.CARET);
                result[i] = new Patient();
                result[i].LocalPid = flds[0];
                result[i].Name = new PersonName(flds[1]);
            }
            return result;
        }

        public string getStateIEN(string stateAbbr)
        {
            if (!State.isValidAbbr(stateAbbr))
            {
                throw new InvalidlyFormedRecordIdException(stateAbbr);
            }
            string arg = "$O(^DIC(5,\"C\",\"" + stateAbbr + "\",0))";
            return VistaUtils.getVariableValue(cxn, arg);
        }

        public string getStateName(string ien)
        {
            if (!VistaUtils.isWellFormedIen(ien))
                return "";

            string arg = "$P($G(^DIC(5," + ien + ",0)),U,1)";
            return VistaUtils.getVariableValue(cxn, arg);
        }

        public string[] getPersonalPhones(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            string arg = "$G(^DPT(" + dfn + ",.13))";
            string response = VistaUtils.getVariableValue(cxn, arg);
            if (response == "")
            {
                return null;
            }
            string[] flds = StringUtils.split(response, StringUtils.CARET);
            if (flds.Length < 3 || (flds[0] == "" && flds[3] == ""))
            {
                return null;
            }
            string[] result = new string[2];
            result[0] = StringUtils.removeNonNumericChars(flds[0]);
            if (flds.Length > 3)
            {
                result[1] = StringUtils.removeNonNumericChars(flds[3]);
            }
            else
            {
                result[1] = "";
            }
            return result;
        }

        public string getCountyName(string stateIen, string countyIen)
        {
            if (!VistaUtils.isWellFormedIen(stateIen))
            {
                throw new InvalidlyFormedRecordIdException("State IEN: " + stateIen);
            }
            // countyIen allows 0
            if (!StringUtils.isNumeric(countyIen))
            {
                throw new InvalidlyFormedRecordIdException("County IEN: " + countyIen);
            }
            DdrLister query = new DdrLister(cxn);
            query.File = "5.01";
            query.Fields = ".01";
            query.Flags = "IP";
            query.Iens = "," + stateIen + ",";
            query.From = VistaUtils.adjustForNumericSearch(countyIen);
            query.Xref = "#";
            string[] response = query.execute();
            if (response.Length == 0)
                return "";
            return StringUtils.piece(response[0], StringUtils.CARET, 2);
        }

        public Patient[] matchByNameCityState(string name, string city, string stateAbbr)
        {
            if (StringUtils.isEmpty(name) || StringUtils.isEmpty(city) || !State.isValidAbbr(stateAbbr))
            {
                return null;
            }
            string stateIEN = getStateIEN(stateAbbr.ToUpper());
            return matchByNameCityStateIEN(name, city, stateIEN);
        }

        public Patient[] matchByNameCityStateIEN(string name, string city, string stateIEN)
        {
            DdrLister query = new DdrLister(cxn);
            query.File = "2";
            query.Fields = ".01;.02;.03;.09;.111;.112;.113;.114;.115E;.116;.117;.131;991.01;991.02";
            query.Flags = "IP";
            query.From = VistaUtils.adjustForNameSearch(name.ToUpper());
            query.Part = name.ToUpper();
            query.Xref = "B";
            query.Screen = "I $D(^(.11))=1, " +
                "$P(^(.11),U,4)[\"" + city.ToUpper() + "\", " +
                "$P(^(.11),U,5)=" + stateIEN;
            string[] response = query.execute();
            return toPatientsFromNameCityState(response, stateIEN);
        }

        internal Patient[] toPatientsFromNameCityState(string[] response, string stateIEN)
        {
            if (response == null || response.Length == 0)
            {
                return null;
            }
            Patient[] result = new Patient[response.Length];
            for (int i = 0; i < response.Length; i++)
            {
                string[] flds = StringUtils.split(response[i], StringUtils.CARET);
                result[i] = new Patient();
                result[i].LocalPid = flds[0];
                result[i].Name = new PersonName(flds[1]);
                result[i].Gender = flds[2];
                result[i].DOB = VistaTimestamp.toUtcString(flds[3]);
                result[i].SSN = new SocSecNum(flds[4]);
                result[i].HomeAddress = new Address();
                result[i].HomeAddress.Street1 = flds[5];
                result[i].HomeAddress.Street2 = flds[6];
                result[i].HomeAddress.Street3 = flds[7];
                result[i].HomeAddress.City = flds[8];
                if (flds[11] != "")
                {
                    result[i].HomeAddress.County = getCountyName(stateIEN, flds[11]);
                }
                result[i].HomeAddress.State = flds[9];
                result[i].HomeAddress.Zipcode = flds[10];
                if (flds[12] != "")
                {
                    try
                    {
                        result[i].HomePhone = new PhoneNum(flds[12]);
                    }
                    catch (Exception e) { }
                }
                result[i].MpiPid = flds[13];
                result[i].MpiChecksum = flds[14];
            }
            return result;
        }

        public string[] matchByNameDOBGender(string name, string dob, string gender)
        {
            DdrLister query = new DdrLister(cxn);
            query.File = "2";
            query.Fields = ".01;.02;.03;.09;.111;.112;.113;.114;.115E;.116;.117;.131;991.01;991.02";
            query.Flags = "IP";
            query.From = VistaUtils.adjustForNameSearch(name);
            query.Part = name;
            query.Xref = "B";
            query.Screen = "I $P(^(0),U,3)=" + VistaTimestamp.fromUtcString(dob) +
                ", $P(^(0),U,2)=\"" + gender + "\"";
            string[] response = query.execute();
            return response;
        }

        #endregion

        #region Patient selection

        public Patient selectByRpc(string dfn)
        {
            MdoQuery request = buildSelectByRpcRequest(dfn);
            string response = (string)cxn.query(request, new MenuOption(VistaConstants.CPRS_CONTEXT));
            return toPatientSelectedByRpc(response, dfn);
        }

        internal MdoQuery buildSelectByRpcRequest(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            VistaQuery vq = new VistaQuery("ORWPT SELECT");
            vq.addParameter(vq.LITERAL, dfn);
            return vq;
        }

        internal Patient toPatientSelectedByRpc(string response, string dfn)
        {
            if (String.IsNullOrEmpty(response))
            {
                throw new MdoException(MdoExceptionCode.VISTA_NON_SPECIFIC_ERROR, "No such patient");
            }
            string[] fields = StringUtils.split(response, StringUtils.CARET);
            if (fields[0] == "-1")
            {
                throw new MdoException(MdoExceptionCode.VISTA_NON_SPECIFIC_ERROR, fields[5]);
            }
            Patient patient = new Patient();
            patient.Name = new PersonName(fields[0]);
            patient.Gender = fields[1];
            patient.DOB = DateUtils.trimTime(VistaTimestamp.toUtcString(fields[2]));
            patient.SSN = new SocSecNum(fields[3]);
            patient.LocalPid = dfn;
            patient.MpiPid = fields[13];
            if (!StringUtils.isEmpty(fields[14]))
            {
                patient.Age = Convert.ToInt16(fields[14]);
            }
            if (fields[4] != "" && fields[5] != "")
            {
                HospitalLocation hl = new HospitalLocation(fields[4], fields[5]);
                hl.Room = fields[6];
                if (fields[15] != "")
                {
                    hl.Specialty = new KeyValuePair<string, string>(fields[15], "");
                }
                patient.Location = hl;
                patient.IsInpatient = true;
            }
            patient.Cwad = fields[7];
            patient.IsRestricted = (fields[8] == "1");
            if (fields[9] != "")
            {
                patient.AdmitTimestamp = VistaTimestamp.toUtcString(fields[9]);
            }
            patient.IsServiceConnected = (fields[11] == "1");
            if (fields[12] != "")
            {
                patient.ScPercent = Convert.ToInt16(fields[12]);
            }
            cxn.Pid = dfn;
            return patient;
        }

        public Patient select()
        {
            return select(cxn.Pid);
        }

        public Patient select(string dfn)
        {
            return selectPlus(dfn);
        }

        //This call uses no DDR LISTER, just regular RPCs and GET VARIABLE VALUE.
        internal Patient selectPlus(string dfn)
        {
            Patient result = selectByRpc(dfn);
            addNode0(result);
            if (result.IsInpatient)
            {
                addRoomBed(result);
                result.Location.Facility = new Site(cxn.DataSource.SiteId.Id, cxn.DataSource.SiteId.Name);
            }
            result.ActiveInsurance = getActiveInsurance(result.LocalPid);
            result.DeceasedDate = getDeceasedDate(result.LocalPid);
            string ien = getPatientType(result.LocalPid);
            result.PatientType = getPatientTypeValue(ien);
            addNodeMPI(result);
            addNodeVET(result);
            result.Confidentiality = getConfidentiality(result.LocalPid);
            result.PatientFlags = getPatientFlags(result.LocalPid);
            result.SiteIDs = getSiteIDs(result.LocalPid);
            result.Team = getTeam(result.LocalPid);

            result.Demographics = new Dictionary<string, DemographicSet>();
            result.Demographics.Add(cxn.DataSource.SiteId.Id, getDemographics(dfn));

            return result;
        }

        public Patient selectBySSN(string ssn)
        {
            if (!SocSecNum.isWellFormed(ssn))
            {
                throw new InvalidlyFormedRecordIdException(ssn);
            }
            Patient[] matches = match(ssn);
            if (matches == null || matches.Length == 0)
            {
                return null;
            }
            if (matches.Length > 1)
            {
                throw new MdoException(MdoExceptionCode.DATA_INVALID_MULTIPLE_RECORDS, "Multiple SSNs!");
            }
            return select(matches[0].LocalPid);
        }

        public Patient selectByExactName(string name)
        {
            if (!PersonName.isValid(name))
            {
                throw new MdoException(MdoExceptionCode.ARGUMENT_INVALID, "Invalid person name: " + name);
            }
            string arg = "$O(^DPT(\"B\",\"" + name + "\",0))";
            string dfn = VistaUtils.getVariableValue(cxn, arg);
            if (dfn == "")
            {
                return null;
            }
            return select(dfn);
        }

        #endregion

        #region Additional demographics

        public DemographicSet getDemographics()
        {
            return getDemographics(cxn.Pid);
        }

        public DemographicSet getDemographics(string dfn)
        {
            DdrLister query = buildGetDemographicsQuery(dfn);
            string[] response = query.execute();
            return toDemographicSet(response);
        }

        internal DdrLister buildGetDemographicsQuery(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            DdrLister query = new DdrLister(cxn);
            query.File = "2";
            query.Fields = ".111;.112;.113;.114;.115E;.116;.117;.131;.132;.134;.133";
            query.Flags = "IP";
            query.Xref = "#";
            query.From = VistaUtils.adjustForNumericSearch(dfn);
            query.Max = "1";
            return query;
        }

        internal DemographicSet toDemographicSet(string[] response)
        {
            if (response == null || response.Length == 0)
            {
                return null;
            }
            DemographicSet result = new DemographicSet();
            string[] flds = response[0].Split(new char[] { '^' });
            Address addr = new Address();
            if (flds.Length > 0 && flds[1] != "")
            {
                addr.Street1 = flds[1];
            }
            if (flds.Length > 1 && flds[2] != "")
            {
                addr.Street2 = flds[2];
            }
            if (flds.Length > 2 && flds[3] != "")
            {
                addr.Street3 = flds[3];
            }
            if (flds.Length > 3 && flds[4] != "")
            {
                addr.City = flds[4];
            }
            if (flds.Length > 4 && flds[5] != "")
            {
                addr.State = flds[5];
            }
            if (flds.Length > 5 && flds[6] != "")
            {
                addr.Zipcode = flds[6];
            }
            if (flds.Length > 6 && flds[7] != "")
            {
                addr.County = flds[7];
            }
            result.StreetAddresses.Add(addr);

            if (flds.Length > 7 && flds[8] != "")
            {
                PhoneNum p = new PhoneNum(flds[8]);
                p.Description = "Home Phone";
                result.PhoneNumbers.Add(p);
            }
            if (flds.Length > 8 && flds[9] != "")
            {
                PhoneNum p = new PhoneNum(flds[9]);
                p.Description = "Work Phone";
                result.PhoneNumbers.Add(p);
            }
            if (flds.Length > 9 && flds[10] != "")
            {
                PhoneNum p = new PhoneNum(flds[10]);
                p.Description = "Cell Phone";
                result.PhoneNumbers.Add(p);
            }
            if (flds.Length > 10)
            {
                if (flds[11] != "")
                {
                    result.EmailAddresses.Add(new EmailAddress(flds[11]));
                }
            }
            return result;
        }

        internal void addNode0(Patient patient)
        {
            string arg = "$G(^DPT(" + patient.LocalPid + ",0))";
            string response = VistaUtils.getVariableValue(cxn, arg);
            string[] flds = StringUtils.split(response, StringUtils.CARET);
            if (StringUtils.isNumeric(flds[4]))
            {
                patient.MaritalStatus = getMaritalStatusValue(flds[4]);
            }
            if (StringUtils.isNumeric(flds[5]))
            {
                patient.Ethnicity = getEthnicityValue(flds[5]);
            }
            patient.NeedsMeansTest = (flds[13] == "1");
            if (flds.Length > 20)
            {
                patient.IsTestPatient = (flds[20] == "1");
            }
        }

        internal string getMaritalStatusValue(string ien)
        {
            VistaUtils.CheckRpcParams(ien);

            string arg = "$G(^DIC(11," + ien + ",0))";
            string response = VistaUtils.getVariableValue(cxn, arg);
            string[] flds = StringUtils.split(response, StringUtils.CARET);
            return flds[0];
        }

        internal string getEthnicityValue(string ien)
        {
            VistaUtils.CheckRpcParams(ien);

            string arg = "$G(^DIC(10," + ien + ",0))";
            string response = VistaUtils.getVariableValue(cxn, arg);
            string[] flds = StringUtils.split(response, StringUtils.CARET);
            return flds[0];
        }

        internal void addRoomBed(Patient patient)
        {
            string arg = "$G(^DPT(" + patient.LocalPid + ",.101))";
            string response = VistaUtils.getVariableValue(cxn, arg);
            if (response == "")
            {
                return;
            }
            string[] flds = StringUtils.split(response, "-");
            patient.Location.Room = flds[0];
            patient.Location.Bed = flds[1];
        }

        internal void addNodeMPI(Patient patient)
        {
            string arg = "$G(^DPT(" + patient.LocalPid + ",\"MPI\"))";
            string response = VistaUtils.getVariableValue(cxn, arg);
            if (response == "")
            {
                return;
            }
            string[] flds = StringUtils.split(response, StringUtils.CARET);

            if(flds.Length >= 2)
                patient.MpiChecksum = flds[1];

            if (flds.Length >= 3)
                patient.CmorSiteId = flds[2];

            if (flds.Length < 4)
            {
                return;
            }
            patient.IsLocallyAssignedMpiPid = (flds[3] == "1");
        }

        internal void addNodeVET(Patient patient)
        {
            string arg = "$G(^DPT(" + patient.LocalPid + ",\"VET\"))";
            string response = VistaUtils.getVariableValue(cxn, arg);
            string[] flds = StringUtils.split(response, StringUtils.CARET);
            patient.IsVeteran = (flds[0] == "1");
        }
        /// <summary>
        /// Is the Patient record a test patient (not a real patient), just a patient
        /// use to test Vista and related systems.  In Vista the values for this field 
        /// are:
        /// "0" or " " - False
        /// "1"        - True
        /// </summary>
        /// <returns>Boolean</returns>
        /// <remarks>These methods "isTestPatient()..." are called from the PatientApi 
        /// class, but I believe at this time the PatientApi caller method does not 
        /// call these methods.  I created a test case to cover these methods in order 
        /// to have full coverage, and in case these methods ever do get used.
        /// </remarks>
        public bool isTestPatient()
        {
            return isTestPatient(cxn.Pid);
        }

        public bool isTestPatient(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            string arg = "$P(^DPT(" + dfn + ",0),\"^\",21)";
            string response = VistaUtils.getVariableValue(cxn,arg);
            return (response == "1");
        }

        public string getActiveInsurance()
        {
            return getActiveInsurance(cxn.Pid);
        }

        public string getActiveInsurance(string dfn)
        {
            MdoQuery request = buildGetActiveInsuranceRequest(dfn);
            string response = (string)cxn.query(request);
            return response.Trim();
        }

        internal MdoQuery buildGetActiveInsuranceRequest(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            VistaQuery vq = new VistaQuery("ORVAA VAA");
            vq.addParameter(vq.LITERAL, dfn);
            return vq;
        }

        public string getCmor()
        {
            return getCmor(cxn.Pid);
        }

        public string getCmor(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            string arg = "$G(^DPT(" + dfn + ",\"MPI\"))";
            string response = VistaUtils.getVariableValue(cxn,arg);
            return extractCmor(response);
        }

        internal string extractCmor(string response)
        {
            if (response == "")
            {
                return "";
            }
            return StringUtils.piece(response, StringUtils.CARET, 3);
        }

        public string getDeceasedDate()
        {
            return getDeceasedDate(cxn.Pid);
        }

        public string getDeceasedDate(string dfn)
        {
            MdoQuery request = buildGetDeceasedDateRequest(dfn);
            string response = (string)cxn.query(request);
            return toDeceasedDate(response);
        }

        internal MdoQuery buildGetDeceasedDateRequest(string dfn)
        {
            VistaQuery vq = new VistaQuery("ORWPT DIEDON");
            vq.addParameter(vq.LITERAL, dfn);
            return vq;
        }

        internal string toDeceasedDate(string response)
        {
            if (response == "")
            {
                return "";
            }
            return VistaTimestamp.toUtcString(response);
        }

        public HospitalLocation getInpatientLocation()
        {
            return getInpatientLocation(cxn.Pid);
        }

        public HospitalLocation getInpatientLocation(string dfn)
        {
            MdoQuery request = buildGetInpatientLocationRequest(dfn);
            string response = (string)cxn.query(request);
            return toInpatientLocation(response);
        }

        internal MdoQuery buildGetInpatientLocationRequest(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            VistaQuery vq = new VistaQuery("ORWPT INPLOC");
            vq.addParameter(vq.LITERAL, dfn);
            return vq;
        }

        internal HospitalLocation toInpatientLocation(string response)
        {
            if (response == "" || response == "0^^")
            {
                return null;
            }
            string[] flds = StringUtils.split(response, StringUtils.CARET);
            return new HospitalLocation(flds[0], flds[1]); //, flds[2]);
        }

        public OEF_OIF[] getOefOif()
        {
            return getOefOif(cxn.Pid);
        }

        public OEF_OIF[] getOefOif(string dfn)
        {
            DdrLister query = buildGetOefOifQuery(dfn);
            string[] response = query.execute();
            return toOefOif(response);
        }

        internal DdrLister buildGetOefOifQuery(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            DdrLister query = new DdrLister(cxn);
            query.File = "2.3215IS";
            query.Iens = "," + dfn + ",";

            // E flag note
            query.Fields = ".01E;.02;.03;.04;.05;.06;.06E";

            query.Flags = "IP";
            query.Xref = "#";
            return query;
        }

        internal OEF_OIF[] toOefOif(string[] response)
        {
            if (response == null || response.Length == 0)
            {
                return null;
            }
            OEF_OIF[] result = new OEF_OIF[response.Length];
            for (int i = 0; i < response.Length; i++)
            {
                string[] flds = StringUtils.split(response[i], StringUtils.CARET);
                result[i] = new OEF_OIF();
                result[i].Location = flds[1];
                result[i].FromDate = VistaTimestamp.toDateTime(flds[2]);
                if (flds[3] != "")
                {
                    result[i].ToDate = VistaTimestamp.toDateTime(flds[3]);
                }
                result[i].DataLocked = (flds[4] == "1");
                result[i].RecordedDate = VistaTimestamp.toDateTime(flds[5]);
                if (flds[6] != "")
                {
                    result[i].RecordingSite = new KeyValuePair<string, string>(flds[6], flds[7]);
                }
            }
            return result;
        }

        public KeyValuePair<int, string> getConfidentiality()
        {
            return getConfidentiality(cxn.Pid);
        }

        public KeyValuePair<int, string> getConfidentiality(string dfn)
        {
            MdoQuery request = buildGetConfidentialityRequest(dfn);
            string response = (string)cxn.query(request);
            return toConfidentialityResponse(response);
        }

        internal MdoQuery buildGetConfidentialityRequest(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            VistaQuery vq = new VistaQuery("DG SENSITIVE RECORD ACCESS");
            vq.addParameter(vq.LITERAL, dfn);
            return vq;
        }

        internal KeyValuePair<int, string> toConfidentialityResponse(string response)
        {
            if (response == "")
            {
                throw new MdoException(MdoExceptionCode.VISTA_DATA_ERROR, "Blank return from sensitivity check");
            }
            if (response.StartsWith("-1"))
            {
                String errmsg = "Error getting sensitivity: " + response.Substring(4);
                throw new MdoException(MdoExceptionCode.VISTA_DATA_ERROR, errmsg);
            }
            int level = 0;
            String message = "";
            level = Convert.ToInt16(response[0]) - 48;
            if (level != 0)
            {
                int p = response.IndexOf("\r\n");
                message = response.Substring(p + 2);
            }
            return new KeyValuePair<int, string>(level, message);
        }

        public string issueConfidentialityBulletin()
        {
            return issueConfidentialityBulletin(cxn.Pid);
        }

        public string issueConfidentialityBulletin(string dfn)
        {
            // check to see if bulletin has already been issued
            if (cxn is VistaConnection &&
                (cxn as VistaConnection).IssedBulletin.ContainsKey(dfn) && 
                (cxn as VistaConnection).IssedBulletin[dfn])
            {
                return "Sent";
            }
            // if bulletin hasn't been issued - send it
            MdoQuery request = buildIssueConfidentialityBulletinRequest(dfn);
            string response = (string)cxn.query(request);
            if (response != "1")
            {
                return "Unable to send sensitivity bulletin: " + response;
            }
            // set bulletin to issued for this patient id on this connection
            if (cxn is VistaConnection)
            {
                if (!(cxn as VistaConnection).IssedBulletin.ContainsKey(dfn))
                {
                    (cxn as VistaConnection).IssedBulletin.Add(dfn, true);
                }
                else
                {
                    (cxn as VistaConnection).IssedBulletin[dfn] = true;
                }
            }
            return "Sent";
        }

        internal MdoQuery buildIssueConfidentialityBulletinRequest(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            VistaQuery vq = new VistaQuery("DG SENSITIVE RECORD BULLETIN");
            vq.addParameter(vq.LITERAL, dfn);
            return vq;
        }

        public bool needsMeansTest(string dfn)
        {
            MdoQuery request = buildNeedsMeansTestRequest(dfn);
            string response = (string)cxn.query(request);
            return (response == "1");
        }

        internal MdoQuery buildNeedsMeansTestRequest(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            VistaQuery vq = new VistaQuery("DG CHK PAT/DIV MEANS TEST");
            vq.addParameter(vq.LITERAL, dfn);
            return vq;
        }

        public StringDictionary getRemoteSiteIds()
        {
            return getRemoteSiteIds(cxn.Pid);
        }

        public StringDictionary getRemoteSiteIds(string dfn)
        {
            MdoQuery request = buildGetRemoteSitesRequest(dfn);
            string response = (string)cxn.query(request);
            return toSiteIds(response);
        }

        internal StringDictionary toSiteIds(string response)
        {
            if (response == "")
            {
                return null;
            }
            string[] lines = StringUtils.split(response, StringUtils.CRLF);
            StringDictionary result = new StringDictionary();
            for (int linenum = 0; linenum < lines.Length; linenum++)
            {
                if (lines[linenum] == "")
                {
                    continue;
                }
                string[] flds = StringUtils.split(lines[linenum], StringUtils.CARET);
                if (flds[0] == "-1")
                {
                    throw new Exception(flds[1]);
                }
                result.Add(flds[0], flds[1]);
            }
            return result;
        }

        public SiteId[] getSiteIDs(string dfn)
        {
            MdoQuery request = buildGetRemoteSitesRequest(dfn);
            string response = (string)cxn.query(request);
            if (response.StartsWith("-1^"))
            {
                return new SiteId[] { new SiteId(cxn.DataSource.SiteId.Id, cxn.DataSource.SiteId.Name) };
            }
            string[] lines = StringUtils.split(response, StringUtils.CRLF);
            lines = StringUtils.trimArray(lines);
            // SLC's test Vista reports the local site as one of the patient's remote sites. We should
            // make sure we don't add the same site to this collection twice
            Dictionary<string, SiteId> siteDict = new Dictionary<string, SiteId>();
            SiteId connectedSite = new SiteId(cxn.DataSource.SiteId.Id, cxn.DataSource.SiteId.Name);
            siteDict.Add(connectedSite.Id, connectedSite);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] flds = StringUtils.split(lines[i], StringUtils.CARET);
                SiteId current = new SiteId(flds[0], flds[1], flds[2], flds[3]);
                if (siteDict.ContainsKey(current.Id))
                {
                    continue;
                }
                siteDict.Add(current.Id, current);
            }
            SiteId[] result = new SiteId[siteDict.Count];
            siteDict.Values.CopyTo(result, 0);
            return result;
        }

        public Site[] getRemoteSites()
        {
            return getRemoteSites(cxn.Pid);
        }

        public Site[] getRemoteSites(string dfn)
        {
            MdoQuery request = buildGetRemoteSitesRequest(dfn);
            string response = (string)cxn.query(request);
            return toRemoteSites(response);
        }

        internal MdoQuery buildGetRemoteSitesRequest(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            VistaQuery vq = new VistaQuery("ORWCIRN FACLIST");
            vq.addParameter(vq.LITERAL, dfn);
            return vq;
        }

        internal Site[] toRemoteSites(string response)
        {
            if (response == "")
            {
                return null;
            }
            string[] lines = StringUtils.split(response, StringUtils.CRLF);
            ArrayList lst = new ArrayList(lines.Length);
            for (int linenum = 0; linenum < lines.Length; linenum++)
            {
                if (lines[linenum] == "")
                {
                    continue;
                }
                string[] flds = StringUtils.split(lines[linenum], StringUtils.CARET);
                if (flds[0] == "-1")
                {
                    throw new Exception(flds[1]);
                }
                Site s = new Site(flds[0], flds[1]);
                if (flds[2] != "")
                {
                    s.LastEventTimestamp = VistaTimestamp.toUtcString(flds[2]);
                }
                if (flds[3] != "")
                {
                    s.LastEventReason = flds[3];
                }
                lst.Add(s);
            }
            return (Site[])lst.ToArray(typeof(Site));
        }

        public Dictionary<string, string> getTreatingFacilityIds(string pid)
        {
            MdoQuery request = buildGetTreatingFacilityIdsRequest(pid);
            string response = (string)cxn.query(request, new MenuOption(VistaConstants.CAPRI_CONTEXT));
            return toTreatingFacilityIds(response, pid);
        }

        internal MdoQuery buildGetTreatingFacilityIdsRequest(string dfn)
        {
            Decimal lDfn = 0;
            if (!Decimal.TryParse(dfn, out lDfn))
            {
                throw new MdoException(MdoExceptionCode.ARGUMENT_INVALID_NUMERIC_REQUIRED, "Patient ID must be numeric");
            }

            DdrLister ddr = new DdrLister(this.cxn);
            ddr.File = "391.91";
            ddr.Fields = ".01;.02";
            ddr.From = dfn.Substring(0, dfn.Length - 1);
            ddr.Part = dfn;
            ddr.Xref = "B";
            ddr.Flags = "IP";
            ddr.Id = "S ID=$G(^(1,1,0)) D EN^DDIOL(ID)";

            return ddr.buildRequest();
        }

        internal Dictionary<string, string> toTreatingFacilityIds(string response, string expectedDfn)
        {
            if (String.IsNullOrEmpty(response))
            {
                return new Dictionary<string, string>();
            }

            string[] lines = response.Split(new string[] { StringUtils.CRLF }, StringSplitOptions.RemoveEmptyEntries);

            if (lines == null || lines.Length == 0)
            {
                return new Dictionary<string, string>();
            }

            Dictionary<string, string> result = new Dictionary<string, string>();

            foreach (string line in lines)
            {
                string massagedLine = line.Replace("&#94;", "^");
                string[] flds = massagedLine.Split(new string[] { StringUtils.CARET }, StringSplitOptions.None);
                if (flds == null || flds.Length == 0 || flds.Length != 5 || String.IsNullOrEmpty(flds[1]))
                {
                    continue;
                }
                if (!String.Equals(expectedDfn, flds[1])) // make sure the DFN from DDR matches the DFN passed in to the call
                {
                    continue;
                }
                if (!String.Equals("A", flds[4], StringComparison.CurrentCultureIgnoreCase)) // make sure status is active
                {
                    continue;
                }
                result.Add(flds[2], flds[3]);
            }

            return result;
        }

        public StringDictionary getPatientTypes()
        {
            VistaSystemFileHandler h = new VistaSystemFileHandler(cxn);
            return h.getLookupTable("391");
        }

        public string getPatientType(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            string arg = "$G(^DPT(" + dfn + ",\"TYPE\"))";
            string response = VistaUtils.getVariableValue(cxn, arg);
            return response;
        }

        public string getPatientTypeValue(string ien)
        {
            if (!VistaUtils.isWellFormedIen(ien))
            {
                throw new InvalidlyFormedRecordIdException(ien);
            }
            string arg = "$G(^DG(391," + ien + ",0))";
            string response = VistaUtils.getVariableValue(cxn, arg);
            if (response == "")
            {
                return "";
            }
            return StringUtils.piece(response, StringUtils.CARET, 1);
        }

        /// <remarks>
        /// Some bad data was found being returned in Boston for patient 105899 - "1^" is returned from the 
        /// "$G(^DPT(" + 105899 + ",\"TYPE\"))" call above causing the subsequent getPatientTypeValue call to 
        /// throw an exception. The call below seems to succeed for this patient and others. The only downside is
        /// for pre-BSE MDWS where we must switch context before executing this DDR call
        /// </remarks>
        //public string getPatientTypeByDfn(string dfn)
        //{
            
        //    long dfnInt = 0;
        //    if (!Int64.TryParse(dfn, out dfnInt))
        //    {
        //        throw new MdoException(MdoExceptionCode.ARGUMENT_INVALID_NUMERIC_REQUIRED, "The patient's DFN must be numeric");
        //    }
        //    DdrLister ddr = new DdrLister(this.cxn);
        //    ddr.Fields = ".01";
        //    ddr.File = "2";
        //    ddr.Flags = "IP";
        //    ddr.From = (dfnInt - 1).ToString();
        //    ddr.Id = "S X=$$GET1^DIQ(2,DA_\",\",\"TYPE\") D EN^DDIOL(X)";
        //    ddr.Xref = "#";
        //    ddr.Max = "1";

        //    //string response = (string)cxn.query(ddr.buildRequest());
        //    string[] response = ddr.execute();
        //    if (response == null || response.Length != 1 || String.IsNullOrEmpty(response[0]) || !response[0].Contains("^"))
        //    {
        //        return "";
        //    }
        //    string[] flds = response[0].Split(new char[] { '^' });
        //    if (flds == null || flds.Length < 3)
        //    {
        //        return "";
        //    }
        //    return flds[2]; // IEN^PATIENTNAME^TYPE
        //}

        public Team getTeam(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            VistaQuery vq = new VistaQuery("ORWPT1 PRCARE");
            vq.addParameter(vq.LITERAL, dfn);
            string response = (string)cxn.query(vq);
            if (response == "^^")
            {
                return null;
            }
            string[] flds = StringUtils.split(response, StringUtils.CARET);
            return new Team("", flds[0], flds[1], flds[2]);
        }

        public void addHomeData(Patient patient)
        {
            VistaUtils.CheckRpcParams(patient.LocalPid);
            string arg = "$G(^DPT(" + patient.LocalPid + ",.11))";
            string response = VistaUtils.getVariableValue(cxn, arg);
            if (response == "")
            {
                return;
            }
            string[] flds = StringUtils.split(response, StringUtils.CARET);
            patient.HomeAddress = new Address();
            patient.HomeAddress.Street1 = flds[0];
            patient.HomeAddress.Street2 = flds[1];
            patient.HomeAddress.Street3 = flds[2];
            patient.HomeAddress.City = flds[3];
            if (flds[4] != "")
            {
                patient.HomeAddress.State = getStateName(flds[4]);
            }
            patient.HomeAddress.Zipcode = flds[5];
            if (flds[4] != "" && flds[6] != "")
            {
                patient.HomeAddress.County = getCountyName(flds[4], flds[6]);
            }
            string[] phones = getPersonalPhones(patient.LocalPid);
            if (phones != null)
            {
                if (phones[0] != "")
                {
                    patient.HomePhone = new PhoneNum(phones[0]);
                }
                if (phones[1] != "")
                {
                    patient.CellPhone = new PhoneNum(phones[1]);
                }
            }
        }

        internal string getWad(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            string plus = "_U_";
            string arg = "$G(^DPT(" + dfn + ",0))" + plus;
            arg += "$P($G(^DPT(" + dfn + ",.35)),U,1)" + plus;
            arg += "$P($G(^DPT(" + dfn + ",.101)),U,1)" + plus;
            arg += "$G(^DPT(" + dfn + ",\"MPI\"))" + plus;
            arg += "$G(^DPT(" + dfn + ",\"TYPE\"))";
            return VistaUtils.getVariableValue(cxn, arg);
        }

        public string patientInquiry(string pid)
        {
            MdoQuery query = buildPatientInquiry(pid);
            string response = (string)cxn.query(query);
            return response;
        }

        internal MdoQuery buildPatientInquiry(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            VistaQuery vq = new VistaQuery("ORWPT PTINQ");
            vq.addParameter(vq.LITERAL, dfn);
            return vq;
        }

        #endregion

        #region Patient Record Flags

        public string getPatientFlagNoteTitle(string flagDefinitionId)
        {
            return getPatientFlagNoteTitle(cxn.Pid, flagDefinitionId);
        }

        public string getPatientFlagNoteTitle(string dfn, string flagDefinitionId)
        {
            MdoQuery request = buildGetPatientFlagNoteTitleRequest(dfn, flagDefinitionId);
            string response = (string)cxn.query(request);
            return response;
        }

        //4/15/2011 DP Removed an extra parameter that was causing the RPC to
        //             fail.   Ticket #2675
        internal MdoQuery buildGetPatientFlagNoteTitleRequest(string dfn, string flagDefinitionId)
        {
            VistaUtils.CheckRpcParams(dfn);
            if (!VistaUtils.isWellFormedIen(flagDefinitionId))
            {
                throw new InvalidlyFormedRecordIdException(flagDefinitionId);
            }
            VistaQuery vq = new VistaQuery("TIU GET PRF TITLE");
            vq.addParameter(vq.LITERAL, dfn);
            vq.addParameter(vq.LITERAL, flagDefinitionId);
            return vq;
        }

        public string getPatientFlagText(string flagId)
        {
            return getPatientFlagText(cxn.Pid, flagId);
        }

        public string getPatientFlagText(string dfn, string flagId)
        {
            MdoQuery request = buildGetPatientFlagTextRequest(dfn, flagId);
            string response = (string)cxn.query(request);
            return response;
        }

        internal MdoQuery buildGetPatientFlagTextRequest(string dfn, string flagId)
        {
            VistaUtils.CheckRpcParams(dfn);
            if (!VistaUtils.isWellFormedIen(flagId))
            {
                throw new InvalidlyFormedRecordIdException(flagId);
            }
            VistaQuery vq = new VistaQuery("ORPRF GETFLG");
            vq.addParameter(vq.LITERAL, dfn);
            vq.addParameter(vq.LITERAL, flagId);
            return vq;
        }

        public StringDictionary getPatientFlags()
        {
            return getPatientFlags(cxn.Pid);
        }

        public StringDictionary getPatientFlags(string dfn)
        {
            MdoQuery request = buildGetPatientFlagsRequest(dfn);
            string response = (string)cxn.query(request);
            return toPatientFlags(response);
        }

        internal MdoQuery buildGetPatientFlagsRequest(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            VistaQuery vq = new VistaQuery("ORPRF HASFLG");
            vq.addParameter(vq.LITERAL, dfn);
            return vq;
        }

        internal StringDictionary toPatientFlags(string response)
        {
            if (response == "")
            {
                return null;    //Error?
            }
            string[] lines = StringUtils.split(response, StringUtils.CRLF);
            StringDictionary result = new StringDictionary();
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i] == "")
                {
                    continue;
                }
                String[] flds = StringUtils.split(lines[i], StringUtils.CARET);
                result.Add(flds[0], flds[1]);
            }
            return result;
        }

        #endregion

        #region Patient Associates

        public PatientAssociate[] getPatientAssociates()
        {
            return getPatientAssociates(cxn.Pid);
        }

        public PatientAssociate[] getPatientAssociates(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            string arg = "$G(^DPT(" + dfn + ",.21))";
            arg += "_\"|\"_$G(^DPT(" + dfn + ",.211))";
            arg += "_\"|\"_$G(^DPT(" + dfn + ",.33))";
            arg += "_\"|\"_$G(^DPT(" + dfn + ",.331))";
            arg += "_\"|\"_$G(^DPT(" + dfn + ",.34))";
            arg += "_\"|\"_$G(^DPT(" + dfn + ",.29))";
            arg += "_\"|\"_$G(^DPT(" + dfn + ",.291))";
            string response = VistaUtils.getVariableValue(cxn,arg);
            if (response == "")
            {
                return null;
            }
            string[] lines = StringUtils.split(response, "|");
            ArrayList lst = new ArrayList(lines.Length);
            PatientAssociate pa = parseAssociateRecord(lines[0]);
            if (pa != null)
            {
                pa.Association = "Primary NOK";
                pa.FacilityName = cxn.DataSource.SiteId.Name;
                lst.Add(pa);
            }
            pa = parseAssociateRecord(lines[1]);
            if (pa != null)
            {
                pa.Association = "Secondary NOK";
                pa.FacilityName = cxn.DataSource.SiteId.Name;
                lst.Add(pa);
            }
            pa = parseAssociateRecord(lines[2]);
            if (pa != null)
            {
                pa.Association = "Primary Emergency Contact";
                pa.FacilityName = cxn.DataSource.SiteId.Name;
                lst.Add(pa);
            }
            pa = parseAssociateRecord(lines[3]);
            if (pa != null)
            {
                pa.Association = "Secondary Emergency Contact";
                pa.FacilityName = cxn.DataSource.SiteId.Name;
                lst.Add(pa);
            }
            pa = parseAssociateRecord(lines[4]);
            if (pa != null)
            {
                pa.Association = "Designee";
                pa.FacilityName = cxn.DataSource.SiteId.Name;
                lst.Add(pa);
            }
            pa = parseGuardianRecord(lines[5]);
            if (pa != null)
            {
                pa.Association = "VA Guardian";
                pa.FacilityName = cxn.DataSource.SiteId.Name;
                lst.Add(pa);
            }
            pa = parseGuardianRecord(lines[6]);
            if (pa != null)
            {
                pa.Association = "Civil Guardian";
                pa.FacilityName = cxn.DataSource.SiteId.Name;
                lst.Add(pa);
            }
            return (PatientAssociate[])lst.ToArray(typeof(PatientAssociate));
        }

        internal PatientAssociate parseAssociateRecord(string record)
        {
            if (record == "")
            {
                return null;
            }
            string[] flds = StringUtils.split(record, StringUtils.CARET);
            PatientAssociate result = new PatientAssociate();
            result.Name = new PersonName(flds[0]);
            if (flds.Length > 1)
            {
                result.RelationshipToPatient = flds[1];
            }
            if (flds.Length > 7)
            {
                result.HomeAddress = new Address();
                result.HomeAddress.Street1 = flds[2];
                result.HomeAddress.Street2 = flds[3];
                result.HomeAddress.Street3 = flds[4];
                result.HomeAddress.City = flds[5];
                result.HomeAddress.State = getStateName(flds[6]);
                result.HomeAddress.Zipcode = flds[7];
            }
            if (flds.Length > 8 && flds[8] != "")
            {
                result.HomePhone = new PhoneNum(flds[8]);
            }
            return result;
        }

        internal PatientAssociate parseGuardianRecord(string record)
        {
            if (record == "" || record == "^^^^^^^^^^^0")
            {
                return null;
            }
            string[] flds = StringUtils.split(record, StringUtils.CARET);
            PatientAssociate result = new PatientAssociate();
            result.Name = new PersonName(flds[3]);
            result.RelationshipToPatient = flds[4];
            result.HomeAddress = new Address();
            result.HomeAddress.Street1 = flds[5];
            result.HomeAddress.Street2 = flds[6];
            result.HomeAddress.City = flds[7];
            result.HomeAddress.State = getStateName(flds[8]);
            result.HomeAddress.Zipcode = flds[9];
            if (flds.Length > 7 && flds[10] != "")
            {
                result.HomePhone = new PhoneNum(flds[10]);
            }
            return result;
        }

        public KeyValuePair<string, string> getPcpForPatient(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            string arg = "$P($$NMPCPR^SCAPMCU2(" + dfn + ",DT,1),U,1,999)";
            MdoQuery request = VistaUtils.buildGetVariableValueRequest(arg);
            string response = (string)cxn.query(request);
            if (String.IsNullOrEmpty(response))
            {
                return new KeyValuePair<string, string>("", "");
            }
            string[] flds = StringUtils.split(response, StringUtils.CARET);
            return new KeyValuePair<string, string>(flds[0], flds[1]);
        }

        #endregion

        #region Rated Disabilities

        public RatedDisability[] getRatedDisabilities()
        {
            return getRatedDisabilities(cxn.Pid);
        }

        public RatedDisability[] getRatedDisabilities(string dfn)
        {
            DdrLister query = buildGetRatedDisabilitiesQuery(dfn);
            string[] response = query.execute();
            return toRatedDisabilities(response);
        }

        internal DdrLister buildGetRatedDisabilitiesQuery(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            DdrLister query = new DdrLister(cxn);
            query.File = "2.04";
            query.Iens = "," + dfn + ",";
            query.Fields = ".01;.01E;2;3;4;5;6";
            query.Flags = "IP";
            query.Xref = "#";
            return query;
        }

        internal RatedDisability[] toRatedDisabilities(string[] response)
        {
            if (response == null || response.Length == 0)
            {
                return null;
            }

            List<RatedDisability> lst = new List<RatedDisability>(response.Length);
            for (int i = 0; i < response.Length; i++)
            {
                if (response[i] == "")
                {
                    continue;
                }
                string[] flds = response[i].Split(new char[] { '^' });
                if (flds.Length == 0)
                {
                    continue;
                }
                RatedDisability disability = new RatedDisability();
                if (flds.Length > 0)
                {
                    disability.Id = flds[1];
                }
                if (flds.Length > 1)
                {
                    disability.Name = flds[2];
                }
                if (flds.Length > 2)
                {
                    disability.Percent = flds[3];
                }
                if (flds.Length > 3)
                {
                    disability.ServiceConnected = (flds[4] == "1");
                }
                if (flds.Length > 4)
                {
                    disability.ExtremityAffected = flds[5];
                }
                if (flds.Length > 5)
                {
                    disability.OriginalEffectiveDate = VistaTimestamp.toUtcString(flds[6]);
                }
                if (flds.Length > 6)
                {
                    disability.CurrenEffectiveDate = VistaTimestamp.toUtcString(flds[7]);
                }
                lst.Add(disability);
            }
            return (RatedDisability[])lst.ToArray();
        }

        #endregion

        #region Miscellaneous

        public bool hasPatient(string dfn)
        {
            VistaUtils.CheckRpcParams(dfn);
            string arg = "$D(^DPT(" + dfn + ",0))";
            MdoQuery request = VistaUtils.buildGetVariableValueRequest(arg);
            string response = (string)cxn.query(request);
            return response == "1";
        }

        public string getLocalPid(string icn)
        {
            if (!StringUtils.isNumeric(icn))
            {
                throw new InvalidlyFormedRecordIdException(icn);
            }
            string arg = "$O(^DPT(\"AICN\"," + icn + ",0))";
            string response = VistaUtils.getVariableValue(cxn, arg);
            cxn.Pid = response;
            return response;
        }

        public string getLastRecordNumber()
        {
            string arg = "^DPT(0)";
            string response = VistaUtils.getVariableValue(cxn, arg);
            return StringUtils.piece(response, "^", 3);
        }

        #endregion



        public TextReport getMOSReport(Patient patient)
        {
            throw new NotImplementedException();
        }
    }
}
