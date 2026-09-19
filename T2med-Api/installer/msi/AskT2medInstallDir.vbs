p=Session.Property("T2MEDINSTALLDIR")
If p="" Then p="D:\t2med"
r=InputBox("T2med-Installationsordner:", "T2med", p)
If r="" Then r=p
Session.Property("T2MEDINSTALLDIR")=r
