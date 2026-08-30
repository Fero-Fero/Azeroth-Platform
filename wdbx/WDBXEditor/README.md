# WDBX Editor

### About
This editor has full support for reading and saving all release versions of DBC, DB2, WDB, ADB and DBCache. This does include support for Legion DB2 and DBCache files and works with all variants (header flags) of these.
Like the other editors I've used a definition based system whereby definitions tell the editor how to interpret each file's columns - this is a lot more reliable than guessing column types but does mean the definitions must be maintained. So far, I've mapped almost all expansions with MoP being ~50% complete and everything else being 99%+ (excluding column names).

You will need **Microsoft .NET Framework 4.8** to run this application

### Tools:
- Definition editor for maintaining the definitions
- WotLK Item Import to remove the dreaded red question mark from custom items
- Legion Parser which is an attempt to automatically parse the structure of WDB5 and WDB6 files

### Project Goal:
The goal of this project is to create a communal program that is compatible with all file variants, is feature rich and negates the need to use multiple different programs.
This means any and all contribution in the form of commits, change requests, issues etc are more than welcome!

### MySQL Setup:
LOAD DATA LOCAL INFILE is not enabled by default.Normally, it should be enabled by placing local-infile=1 in my.cnf. But it does not work for all installations.
Connect to your server using MySQL or any console client using the following command:

```sql
mysql -u root -p 
```

It should echo the following:

```sql
Enter password:
```

Now you have to enter your root password. After you are connected, paste the following command:

```sql
SHOW GLOBAL VARIABLES LIKE 'local_infile';
```

It should echo the following:

```sql
+---------------+-------+
| Variable_name | Value |
+---------------+-------+
| local_infile  | OFF   |
+---------------+-------+
1 row in set (0.00 sec)
```

If local_infile = OFF, you need to paste the following command, otherwise if it is ON, you are good to go!

```sql
SET GLOBAL local_infile = 'ON'
```

It should echo the following:

```sql
+---------------+-------+
| Variable_name | Value |
+---------------+-------+
| local_infile  | ON    |
+---------------+-------+
1 row in set (0.00 sec)
```
