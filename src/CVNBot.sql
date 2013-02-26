-- 
-- MySQL Database structure for CVNBot (expiremental)
--

-- --------------------------------------------------------

--
-- Table structure for table `items`
--

CREATE TABLE `items` (
  `item` varchar(80) DEFAULT NULL,
  `itemtype` int(2) DEFAULT NULL,
  `adder` varchar(64) DEFAULT NULL,
  `reason` varchar(80) DEFAULT NULL,
  `expiry` bigint(64) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;


-- --------------------------------------------------------

--
-- Table structure for table `users`
--

CREATE TABLE `users` (
  `name` varchar(64) DEFAULT NULL,
  `project` varchar(32) DEFAULT NULL,
  `type` int(2) DEFAULT NULL,
  `adder` varchar(64) DEFAULT NULL,
  `reason` varchar(80) DEFAULT NULL,
  `expiry` bigint(64) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;


-- --------------------------------------------------------

--
-- Table structure for table `watchlist`
--

CREATE TABLE `watchlist` (
  `article` varchar(64) DEFAULT NULL,
  `project` varchar(32) DEFAULT NULL,
  `adder` varchar(64) DEFAULT NULL,
  `reason` varchar(80) DEFAULT NULL,
  `expiry` bigint(64) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
